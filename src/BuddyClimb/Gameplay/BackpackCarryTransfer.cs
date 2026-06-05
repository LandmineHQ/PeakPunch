using System.Collections.Generic;
using BuddyClimb.Configuration;
using Photon.Pun;
using UnityEngine;
using Zorro.Core.Serizalization;

namespace BuddyClimb.Gameplay;

internal static class BackpackCarryTransfer
{
    private static readonly byte BackpackSlotIndex = (byte)Player.BACKPACKSLOTINDEX;
    private static readonly Dictionary<int, float> PendingBackpackTransferSyncSuppressions = [];
    private const float PendingBackpackTransferSyncSuppressionSeconds = 2f;

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
            && (!HasBackpack(carried) || CanDropBackpackWithVanillaSlotDrop(carried));
    }

    internal static bool TryPrepareBackpacksForClimb(Character carrier, Character carried)
    {
        if (!AllowsCarrierBackpack
            || carrier == null
            || carried == null
            || !HasBackpack(carrier))
        {
            return true;
        }

        if (!CanTransferCarrierBackpack(carrier, carried))
        {
            return false;
        }

        if (HasBackpack(carried))
        {
            return TryDropCarriedBackpackAndTransferOnMaster(carrier, carried)
                || TryRequestMasterDropCarriedBackpackAndTransfer(carrier, carried);
        }

        return TryTransferCarrierBackpack(carrier, carried, syncInventory: true);
    }

    internal static bool TryDropCarriedBackpackAndTransferOnMaster(Character carrier, Character carried)
    {
        if (!PhotonNetwork.IsMasterClient)
        {
            return false;
        }

        if (!CanTransferCarrierBackpack(carrier, carried))
        {
            return false;
        }

        BackpackSlotSnapshot carrierBackpackSnapshot = BackpackSlotSnapshot.Capture(carrier.player);
        BackpackSlotSnapshot carriedBackpackSnapshot = BackpackSlotSnapshot.Capture(carried.player);
        int droppedItemsStartCount = GetDroppedItemCount(carried);

        try
        {
            if (HasBackpack(carried) && !TryDropCarriedBackpackWithVanillaSlotDrop(carried))
            {
                RollBackBackpackTransfer(
                    carrier,
                    carried,
                    carrierBackpackSnapshot,
                    carriedBackpackSnapshot,
                    droppedItemsStartCount,
                    "the carried backpack could not be dropped");
                return false;
            }

            if (!TryTransferCarrierBackpack(carrier, carried, syncInventory: true))
            {
                RollBackBackpackTransfer(
                    carrier,
                    carried,
                    carrierBackpackSnapshot,
                    carriedBackpackSnapshot,
                    droppedItemsStartCount,
                    "the carrier backpack could not be transferred");
                return false;
            }

            return true;
        }
        catch (System.Exception ex)
        {
            RollBackBackpackTransfer(
                carrier,
                carried,
                carrierBackpackSnapshot,
                carriedBackpackSnapshot,
                droppedItemsStartCount,
                ex.ToString());
            return false;
        }
    }

    private static bool TryRequestMasterDropCarriedBackpackAndTransfer(Character carrier, Character carried)
    {
        BackpackTransferRpc transferRpc = BackpackTransferRpc.Ensure(carried);
        transferRpc.RequestDropCarriedBackpackAndTransfer(carrier.photonView, carried.photonView);
        SuppressIncomingEmptyBackpackSync(carried.player);
        ClearCarriedBackpackLocally(carried);
        return TryTransferCarrierBackpack(carrier, carried, syncInventory: false);
    }

    internal static bool ShouldSuppressStaleBackpackEmptySync(Player player, byte[] data, bool forceSync)
    {
        if (player == null || player.photonView == null)
        {
            return false;
        }

        int viewId = player.photonView.ViewID;
        if (!PendingBackpackTransferSyncSuppressions.TryGetValue(viewId, out float expiresAt))
        {
            return false;
        }

        if (Time.realtimeSinceStartup > expiresAt)
        {
            PendingBackpackTransferSyncSuppressions.Remove(viewId);
            return false;
        }

        InventorySyncData inventorySyncData = IBinarySerializable.GetFromManagedArray<InventorySyncData>(data);
        if (inventorySyncData.hasBackpack)
        {
            PendingBackpackTransferSyncSuppressions.Remove(viewId);
            return false;
        }

        if (forceSync)
        {
            PendingBackpackTransferSyncSuppressions.Remove(viewId);
            return false;
        }

        Plugin.Log.LogDebug($"Suppressed stale empty backpack sync for {player.character?.characterName ?? "unknown player"} during BuddyClimb backpack transfer.");
        return true;
    }

    private static bool TryDropCarriedBackpackWithVanillaSlotDrop(Character carried)
    {
        if (carried == null || !HasBackpack(carried))
        {
            return true;
        }

        if (!CanDropBackpackWithVanillaSlotDrop(carried))
        {
            Plugin.Log.LogWarning($"Unable to drop {carried.characterName}'s backpack because CharacterItems is unavailable.");
            return false;
        }

        try
        {
            carried.refs.items.DropItemFromSlotRPC(BackpackSlotIndex, GetBackpackDropPosition(carried));
            return true;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"Unable to drop {carried.characterName}'s backpack through PEAK's slot drop path: {ex}");
            return false;
        }
    }

    private static bool TryTransferCarrierBackpack(Character carrier, Character carried, bool syncInventory)
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
        if (!CanSyncInventory(carrierPlayer) || !CanSyncInventory(carriedPlayer))
        {
            Plugin.Log.LogWarning("Skipping backpack transfer because a Player inventory reference is unavailable.");
            return false;
        }

        if (HasBackpack(carried))
        {
            Plugin.Log.LogWarning($"Skipping backpack transfer because {carried.characterName} is still wearing a backpack.");
            return false;
        }

        BackpackSlot carrierBackpack = carrierPlayer.backpackSlot;
        carriedPlayer.backpackSlot = carrierBackpack;
        carrierPlayer.backpackSlot = new BackpackSlot(BackpackSlotIndex);

        carried.refs.afflictions.UpdateWeight();
        carrier.refs.afflictions.UpdateWeight();

        if (syncInventory)
        {
            SyncInventory(carriedPlayer);
            SyncInventory(carrierPlayer);
        }

        return true;
    }

    private static void RollBackBackpackTransfer(
        Character carrier,
        Character carried,
        BackpackSlotSnapshot carrierBackpackSnapshot,
        BackpackSlotSnapshot carriedBackpackSnapshot,
        int droppedItemsStartCount,
        string reason)
    {
        Plugin.Log.LogWarning($"Rolling back BuddyClimb backpack transfer for {carried.characterName}: {reason}");

        DestroyDroppedItemsAddedSince(carried, droppedItemsStartCount);
        carrierBackpackSnapshot.Restore(carrier.player);
        carriedBackpackSnapshot.Restore(carried.player);
        carrier.refs.afflictions.UpdateWeight();
        carried.refs.afflictions.UpdateWeight();

        try
        {
            SyncInventory(carried.player);
            SyncInventory(carrier.player);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"Unable to sync rollback state after BuddyClimb backpack transfer failed: {ex}");
        }
    }

    private static void DestroyDroppedItemsAddedSince(Character carried, int droppedItemsStartCount)
    {
        List<PhotonView> droppedItems = carried.refs.items.droppedItems;
        if (droppedItems == null)
        {
            return;
        }

        for (int i = droppedItems.Count - 1; i >= droppedItemsStartCount; i--)
        {
            PhotonView droppedItem = droppedItems[i];
            droppedItems.RemoveAt(i);

            if (droppedItem == null)
            {
                continue;
            }

            try
            {
                PhotonNetwork.Destroy(droppedItem);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Unable to destroy rolled-back BuddyClimb dropped backpack: {ex}");
            }
        }
    }

    private static int GetDroppedItemCount(Character character)
    {
        return character.refs.items.droppedItems?.Count ?? 0;
    }

    private static bool HasBackpack(Character character)
    {
        return character.player?.backpackSlot is { hasBackpack: true };
    }

    private static void ClearCarriedBackpackLocally(Character carried)
    {
        carried.player.backpackSlot.EmptyOut();
        carried.refs.afflictions.UpdateWeight();
    }

    private static void SuppressIncomingEmptyBackpackSync(Player player)
    {
        if (player?.photonView == null)
        {
            return;
        }

        PendingBackpackTransferSyncSuppressions[player.photonView.ViewID] =
            Time.realtimeSinceStartup + PendingBackpackTransferSyncSuppressionSeconds;
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

    private static bool CanSyncInventory(Player player)
    {
        return player != null
            && player.photonView != null
            && player.itemSlots != null
            && player.backpackSlot != null
            && player.tempFullSlot != null;
    }

    private readonly struct BackpackSlotSnapshot
    {
        private readonly bool hasBackpack;
        private readonly Item? prefab;
        private readonly ItemInstanceData data;

        private BackpackSlotSnapshot(BackpackSlot backpackSlot)
        {
            hasBackpack = backpackSlot.hasBackpack;
            prefab = backpackSlot.prefab;
            data = backpackSlot.data;
        }

        internal static BackpackSlotSnapshot Capture(Player player)
        {
            return new BackpackSlotSnapshot(player.backpackSlot);
        }

        internal void Restore(Player player)
        {
            BackpackSlot backpackSlot = new(BackpackSlotIndex)
            {
                hasBackpack = hasBackpack,
                prefab = prefab,
                data = data,
            };

            player.backpackSlot = backpackSlot;
        }
    }
}
