using System.Collections.Generic;
using BuddyClimb.Configuration;
using Photon.Pun;
using UnityEngine;
using Zorro.Core;
using Zorro.Core.Serizalization;

namespace BuddyClimb.Gameplay;

internal enum BackpackPreparationResult
{
    Failed,
    Ready,
    Deferred,
}

internal static class BackpackCarryTransfer
{
    private const float PendingTransferTimeoutSeconds = 5f;
    private static readonly byte BackpackSlotIndex = (byte)Player.BACKPACKSLOTINDEX;
    private static readonly Dictionary<int, PendingBackpackTransfer> PendingTransfers = [];

    internal static bool AllowsCarrierBackpack => BuddyClimbConfig.EnableBackpackTransfer.Value;

    internal static bool WillDropCarriedBackpack(Character carrier, Character carried)
    {
        return AllowsCarrierBackpack
            && carrier != null
            && carried != null
            && HasBackpack(carrier)
            && HasBackpack(carried);
    }

    internal static bool CanTransferCarrierBackpack(Character carrier, Character carried)
    {
        if (!AllowsCarrierBackpack
            || carrier == null
            || carried == null
            || !HasBackpack(carrier))
        {
            return true;
        }

        Player carrierPlayer = carrier.player;
        Player carriedPlayer = carried.player;
        return CanSyncInventory(carrierPlayer)
            && CanSyncInventory(carriedPlayer)
            && IsLocallyOwnedPlayer(carriedPlayer)
            && (!HasBackpack(carried) || CanDropBackpackWithVanillaSlotDrop(carried));
    }

    internal static BackpackPreparationResult PrepareBackpacksForClimb(Character carrier, Character carried)
    {
        BuddyClimbDiagnostics.LogCarry($"PrepareBackpacksForClimb entered: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
        PruneExpiredTransfers();

        if (!AllowsCarrierBackpack
            || carrier == null
            || carried == null
            || !HasBackpack(carrier))
        {
            BuddyClimbDiagnostics.LogCarry($"PrepareBackpacksForClimb returning Ready without transfer: allows={AllowsCarrierBackpack} {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
            return BackpackPreparationResult.Ready;
        }

        if (TryGetPendingTransfer(carried, out PendingBackpackTransfer pendingTransfer))
        {
            if (pendingTransfer.Carrier == carrier)
            {
                BuddyClimbDiagnostics.LogCarry($"Ignoring duplicate BuddyClimb backpack transfer request while the first request is pending: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
                return BackpackPreparationResult.Deferred;
            }

            Plugin.Log.LogWarning($"Ignoring BuddyClimb backpack transfer to {carrier.characterName} because another transfer is already pending for {carried.characterName}.");
            return BackpackPreparationResult.Failed;
        }

        if (!CanTransferCarrierBackpack(carrier, carried))
        {
            BuddyClimbDiagnostics.LogCarry($"PrepareBackpacksForClimb returning Failed because CanTransferCarrierBackpack=false: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
            return BackpackPreparationResult.Failed;
        }

        if (!HasBackpack(carried))
        {
            return BeginCarrierBackpackTransfer(carrier, carried, dropCarriedBackpack: false);
        }

        return BeginCarrierBackpackTransfer(carrier, carried, dropCarriedBackpack: true);
    }

    internal static void HandleInventorySync(Player player, byte[] data, bool forceSync)
    {
        if (forceSync || player == null || player.photonView == null || data == null)
        {
            return;
        }

        PendingBackpackTransfer? pendingTransfer = FindPendingTransfer(player);
        if (pendingTransfer == null)
        {
            return;
        }

        if (Time.realtimeSinceStartup > pendingTransfer.ExpiresAt)
        {
            RemovePendingTransfer(pendingTransfer.Carried);
            RestoreCarrierBackpackAfterTimeout(pendingTransfer);
            Plugin.Log.LogWarning($"Timed out waiting for BuddyClimb backpack transfer confirmation for {pendingTransfer.Carried.characterName}.");
            return;
        }

        InventorySyncData inventorySyncData = IBinarySerializable.GetFromManagedArray<InventorySyncData>(data);
        if (inventorySyncData.backpackType != 0 || inventorySyncData.backpackSlot.ItemID != ushort.MaxValue)
        {
            return;
        }

        if (pendingTransfer.Carried.player == player)
        {
            if (!pendingTransfer.NeedsCarriedDrop)
            {
                return;
            }

            pendingTransfer.CarriedSlotCleared = true;
            if (!pendingTransfer.CarrierRemovalRequested && !pendingTransfer.CarrierSlotCleared)
            {
                if (!TryRequestCarrierBackpackRemoval(pendingTransfer.Carrier))
                {
                    RemovePendingTransfer(pendingTransfer.Carried);
                    Plugin.Log.LogWarning($"Unable to request the next BuddyClimb backpack transfer phase for {pendingTransfer.Carried.characterName}.");
                    return;
                }

                pendingTransfer.CarrierRemovalRequested = true;
            }
        }
        else if (pendingTransfer.Carrier.player == player)
        {
            if (!pendingTransfer.CarrierRemovalRequested)
            {
                return;
            }

            pendingTransfer.CarrierSlotCleared = true;
        }
        else
        {
            return;
        }

        if ((pendingTransfer.NeedsCarriedDrop && !pendingTransfer.CarriedSlotCleared)
            || !pendingTransfer.CarrierSlotCleared)
        {
            return;
        }

        RemovePendingTransfer(pendingTransfer.Carried);

        if (!TryApplyTransferredBackpack(
                pendingTransfer.Carrier,
                pendingTransfer.Carried,
                pendingTransfer.CarrierBackpack,
                syncInventory: true))
        {
            Plugin.Log.LogWarning($"Unable to apply the deferred BuddyClimb backpack transfer for {pendingTransfer.Carried.characterName}.");
            return;
        }

        BuddyClimbCarryStarter.TryStartCarry(pendingTransfer.Carrier, pendingTransfer.Carried);
    }

    private static BackpackPreparationResult BeginCarrierBackpackTransfer(
        Character carrier,
        Character carried,
        bool dropCarriedBackpack)
    {
        Player carrierPlayer = carrier.player;
        Player carriedPlayer = carried.player;
        if (!CanSyncInventory(carrierPlayer)
            || !CanSyncInventory(carriedPlayer)
            || !IsLocallyOwnedPlayer(carriedPlayer)
            || (dropCarriedBackpack && !CanDropBackpackWithVanillaSlotDrop(carried)))
        {
            return BackpackPreparationResult.Failed;
        }

        BackpackSlot carrierBackpack = CloneBackpackSlot(carrierPlayer.backpackSlot);
        if (carrierBackpack.IsEmpty())
        {
            return BackpackPreparationResult.Failed;
        }

        ClearCarrierHeldBackpack(carrier);

        if (PhotonNetwork.IsMasterClient)
        {
            try
            {
                if (dropCarriedBackpack)
                {
                    carried.refs.items.DropItemFromSlotRPC(
                        BackpackSlotIndex,
                        GetBackpackDropPosition(carried));
                }

                carrierPlayer.EmptySlot(Optionable<byte>.Some(BackpackSlotIndex), true);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Unable to drop the old carried backpack on the MasterClient: {ex}");
                return BackpackPreparationResult.Failed;
            }

            return TryApplyTransferredBackpack(
                    carrier,
                    carried,
                    carrierBackpack,
                    syncInventory: true)
                ? BackpackPreparationResult.Ready
                : BackpackPreparationResult.Failed;
        }

        try
        {
            if (dropCarriedBackpack)
            {
                RequestCarriedBackpackDrop(carried);
            }

            if (!dropCarriedBackpack && !TryRequestCarrierBackpackRemoval(carrier))
            {
                return BackpackPreparationResult.Failed;
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"Unable to request the BuddyClimb backpack preparation from the MasterClient: {ex}");
            return BackpackPreparationResult.Failed;
        }

        PendingTransfers[carried.photonView.ViewID] = new PendingBackpackTransfer(
            carrier,
            carried,
            carrierBackpack,
            dropCarriedBackpack,
            Time.realtimeSinceStartup + PendingTransferTimeoutSeconds);

        PendingTransfers[carried.photonView.ViewID].CarrierRemovalRequested = !dropCarriedBackpack;

        BuddyClimbDiagnostics.LogCarry($"Waiting for the MasterClient to clear the required backpack slots: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
        return BackpackPreparationResult.Deferred;
    }

    private static void RequestCarriedBackpackDrop(Character carried)
    {
        carried.refs.items.photonView.RPC(
            nameof(CharacterItems.DropItemFromSlotRPC),
            RpcTarget.MasterClient,
            BackpackSlotIndex,
            GetBackpackDropPosition(carried));
    }

    private static bool TryRequestCarrierBackpackRemoval(Character carrier)
    {
        if (!CanSyncInventory(carrier.player))
        {
            return false;
        }

        try
        {
            carrier.player.photonView.RPC(
                nameof(Player.RPCRemoveItemFromSlot),
                RpcTarget.MasterClient,
                BackpackSlotIndex);

            return true;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"Unable to request removal of {carrier.characterName}'s backpack: {ex}");
            return false;
        }
    }

    private static bool TryApplyTransferredBackpack(
        Character carrier,
        Character carried,
        BackpackSlot carrierBackpack,
        bool syncInventory)
    {
        if (carrier == null
            || carried == null
            || carrierBackpack == null
            || carrierBackpack.IsEmpty()
            || !CanSyncInventory(carrier.player)
            || !CanSyncInventory(carried.player)
            || !IsLocallyOwnedPlayer(carried.player))
        {
            return false;
        }

        AssignBackpack(carried.player, carrierBackpack);
        carrier.player.backpackSlot = new BackpackSlot(BackpackSlotIndex);

        carried.refs.afflictions.UpdateWeight();
        carrier.refs.afflictions.UpdateWeight();

        if (syncInventory)
        {
            SyncInventory(carried.player);
            SyncInventory(carrier.player);
        }

        return true;
    }

    internal static void ClearHeldBackpackAfterTransfer(Character carrier)
    {
        if (carrier == null)
        {
            return;
        }

        CharacterItems characterItems = carrier.refs.items;
        if (characterItems == null)
        {
            return;
        }

        bool selectedBackpack = IsBackpackSlotSelected(characterItems);
        bool holdingBackpack = carrier.data.currentItem is Backpack;
        if (!selectedBackpack && !holdingBackpack)
        {
            return;
        }

        if (characterItems.currentSelectedSlot.IsSome)
        {
            characterItems.lastSelectedSlot = characterItems.currentSelectedSlot;
        }

        characterItems.currentSelectedSlot = Optionable<byte>.None;

        if (holdingBackpack)
        {
            characterItems.DestroyHeldItemRpc();
        }

        carrier.player.itemsChangedAction?.Invoke(carrier.player.itemSlots);
        characterItems.onSlotEquipped?.Invoke();
    }

    private static void ClearCarrierHeldBackpack(Character carrier)
    {
        ClearHeldBackpackAfterTransfer(carrier);

        CharacterItems characterItems = carrier.refs.items;
        if (characterItems?.photonView == null)
        {
            return;
        }

        characterItems.photonView.RPC(
            nameof(CharacterItems.DestroyHeldItemRpc),
            RpcTarget.Others);

        characterItems.photonView.RPC(
            nameof(CharacterItems.EquipSlotRpc),
            RpcTarget.Others,
            -1,
            -1);
    }

    private static void AssignBackpack(Player player, BackpackSlot source)
    {
        player.backpackSlot = CloneBackpackSlot(source);
    }

    private static BackpackSlot CloneBackpackSlot(BackpackSlot source)
    {
        BackpackSlot clone = new BackpackSlot(BackpackSlotIndex)
        {
            backpackType = source.backpackType,
        };

        if (!source.IsEmpty())
        {
            clone.SetItem(source.prefab, source.data);
        }
        else
        {
            clone.EmptyOut();
        }

        return clone;
    }

    private static void SyncInventory(Player player)
    {
        byte[] data = IBinarySerializable.ToManagedArray(
            new InventorySyncData(player.itemSlots, player.backpackSlot, player.tempFullSlot));

        player.photonView.RPC(
            nameof(Player.SyncInventoryRPC),
            RpcTarget.All,
            data,
            true);
    }

    private static bool IsBackpackSlotSelected(CharacterItems characterItems)
    {
        return characterItems.currentSelectedSlot.IsSome
            && characterItems.currentSelectedSlot.Value == BackpackSlotIndex;
    }

    private static bool HasBackpack(Character character)
    {
        return character.player != null && !character.player.backpackSlot.IsEmpty();
    }

    private static bool CanDropBackpackWithVanillaSlotDrop(Character character)
    {
        return character.refs.items != null
            && character.refs.items.photonView != null;
    }

    private static Vector3 GetBackpackDropPosition(Character character)
    {
        try
        {
            return character.Center + Vector3.up * 0.5f;
        }
        catch
        {
            return character.transform.position + Vector3.up * 0.5f;
        }
    }

    private static bool CanSyncInventory(Player player)
    {
        return player != null
            && player.photonView != null
            && player.itemSlots != null
            && player.backpackSlot != null
            && player.tempFullSlot != null;
    }

    private static bool IsLocallyOwnedPlayer(Player player)
    {
        return player != null
            && player.photonView != null
            && player.photonView.IsMine;
    }

    private static PendingBackpackTransfer? FindPendingTransfer(Player player)
    {
        foreach (PendingBackpackTransfer pendingTransfer in PendingTransfers.Values)
        {
            if (pendingTransfer.Carrier.player == player || pendingTransfer.Carried.player == player)
            {
                return pendingTransfer;
            }
        }

        return null;
    }

    private static bool TryGetPendingTransfer(Character carried, out PendingBackpackTransfer pendingTransfer)
    {
        pendingTransfer = null!;
        if (carried?.photonView == null
            || !PendingTransfers.TryGetValue(carried.photonView.ViewID, out PendingBackpackTransfer? pending))
        {
            return false;
        }

        pendingTransfer = pending;
        if (Time.realtimeSinceStartup <= pendingTransfer.ExpiresAt)
        {
            return true;
        }

        RemovePendingTransfer(carried);
        RestoreCarrierBackpackAfterTimeout(pendingTransfer);
        pendingTransfer = null!;
        return false;
    }

    private static void RemovePendingTransfer(Character carried)
    {
        if (carried?.photonView != null)
        {
            PendingTransfers.Remove(carried.photonView.ViewID);
        }
    }

    private static void PruneExpiredTransfers()
    {
        List<int>? expiredViewIds = null;
        foreach (KeyValuePair<int, PendingBackpackTransfer> pendingTransfer in PendingTransfers)
        {
            if (Time.realtimeSinceStartup <= pendingTransfer.Value.ExpiresAt)
            {
                continue;
            }

            expiredViewIds ??= [];
            expiredViewIds.Add(pendingTransfer.Key);
        }

        if (expiredViewIds == null)
        {
            return;
        }

        foreach (int viewId in expiredViewIds)
        {
            if (PendingTransfers.Remove(viewId, out PendingBackpackTransfer? pendingTransfer))
            {
                RestoreCarrierBackpackAfterTimeout(pendingTransfer);
            }
        }
    }

    private static void RestoreCarrierBackpackAfterTimeout(PendingBackpackTransfer pendingTransfer)
    {
        if (!pendingTransfer.CarrierRemovalRequested || pendingTransfer.CarrierSlotCleared)
        {
            return;
        }

        if (!CanSyncInventory(pendingTransfer.Carrier.player))
        {
            return;
        }

        AssignBackpack(pendingTransfer.Carrier.player, pendingTransfer.CarrierBackpack);
        SyncInventory(pendingTransfer.Carrier.player);
    }

    private sealed class PendingBackpackTransfer
    {
        internal PendingBackpackTransfer(
            Character carrier,
            Character carried,
            BackpackSlot carrierBackpack,
            bool needsCarriedDrop,
            float expiresAt)
        {
            Carrier = carrier;
            Carried = carried;
            CarrierBackpack = carrierBackpack;
            NeedsCarriedDrop = needsCarriedDrop;
            ExpiresAt = expiresAt;
        }

        internal Character Carrier { get; }

        internal Character Carried { get; }

        internal BackpackSlot CarrierBackpack { get; }

        internal float ExpiresAt { get; }

        internal bool NeedsCarriedDrop { get; }

        internal bool CarriedSlotCleared { get; set; }

        internal bool CarrierRemovalRequested { get; set; }

        internal bool CarrierSlotCleared { get; set; }
    }
}
