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
}

internal static class BackpackCarryTransfer
{
    private static readonly byte BackpackSlotIndex = (byte)Player.BACKPACKSLOTINDEX;

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
            && (!HasBackpack(carried) || CanDropBackpackWithVanillaSlotDrop(carrier));
    }

    internal static BackpackPreparationResult PrepareBackpacksForClimb(Character carrier, Character carried)
    {
        BuddyClimbDiagnostics.LogCarry($"PrepareBackpacksForClimb entered: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");

        if (!AllowsCarrierBackpack
            || carrier == null
            || carried == null
            || !HasBackpack(carrier))
        {
            BuddyClimbDiagnostics.LogCarry($"PrepareBackpacksForClimb returning Ready without transfer: allows={AllowsCarrierBackpack} {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
            return BackpackPreparationResult.Ready;
        }

        if (!CanTransferCarrierBackpack(carrier, carried))
        {
            BuddyClimbDiagnostics.LogCarry($"PrepareBackpacksForClimb returning Failed because CanTransferCarrierBackpack=false: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
            return BackpackPreparationResult.Failed;
        }

        bool hasCarriedBackpack = HasBackpack(carried);
        if (hasCarriedBackpack)
        {
            BackpackSlotSnapshot carrierBackpackSnapshot = BackpackSlotSnapshot.Capture(carrier.player);
            BackpackSlotSnapshot carriedBackpackSnapshot = BackpackSlotSnapshot.Capture(carried.player);

            try
            {
                AssignBackpack(carried.player, carrier.player.backpackSlot);
                AssignBackpack(carrier.player, carriedBackpackSnapshot.RestoredSlot());
                ClearCarrierHeldBackpack(carrier);

                SyncInventory(carried.player);
                SyncInventory(carrier.player);

                if (!TryDropOldCarriedBackpackFromCarrierSlot(carrier))
                {
                    RestoreBackpackSlots(carrier, carried, carrierBackpackSnapshot, carriedBackpackSnapshot);
                    BuddyClimbDiagnostics.LogCarry($"PrepareBackpacksForClimb failed because the carried backpack could not be dropped from the carrier slot: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
                    return BackpackPreparationResult.Failed;
                }

                BuddyClimbDiagnostics.LogCarry($"PrepareBackpacksForClimb swapped backpacks and dropped the old carried backpack from the carrier slot: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
                return BackpackPreparationResult.Ready;
            }
            catch (System.Exception ex)
            {
                RestoreBackpackSlots(carrier, carried, carrierBackpackSnapshot, carriedBackpackSnapshot);
                Plugin.Log.LogWarning($"Unable to complete BuddyClimb double-backpack transfer: {ex}");
                return BackpackPreparationResult.Failed;
            }
        }

        bool transferred = TryTransferCarrierBackpack(carrier, carried, syncInventory: true);
        BuddyClimbDiagnostics.LogCarry($"PrepareBackpacksForClimb transfer without carried backpack result={transferred}: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
        return transferred
            ? BackpackPreparationResult.Ready
            : BackpackPreparationResult.Failed;
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

        AssignBackpack(carriedPlayer, carrierPlayer.backpackSlot);
        carrierPlayer.backpackSlot = new BackpackSlot(BackpackSlotIndex);
        ClearCarrierHeldBackpack(carrier);

        carried.refs.afflictions.UpdateWeight();
        carrier.refs.afflictions.UpdateWeight();

        if (syncInventory)
        {
            SyncInventory(carriedPlayer);
            SyncInventory(carrierPlayer);
        }

        return true;
    }

    private static bool TryDropOldCarriedBackpackFromCarrierSlot(Character carrier)
    {
        if (!HasBackpack(carrier))
        {
            return true;
        }

        if (!CanDropBackpackWithVanillaSlotDrop(carrier))
        {
            Plugin.Log.LogWarning($"Unable to drop the old carried backpack through {carrier.characterName}'s CharacterItems because the CharacterItems or PhotonView is unavailable.");
            return false;
        }

        try
        {
            carrier.refs.items.photonView.RPC(
                nameof(CharacterItems.DropItemFromSlotRPC),
                RpcTarget.All,
                BackpackSlotIndex,
                GetBackpackDropPosition(carrier));

            SyncInventory(carrier.player);
            return true;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"Unable to drop the old carried backpack through {carrier.characterName}'s backpack slot: {ex}");
            return false;
        }
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

    private static void RestoreBackpackSlots(
        Character carrier,
        Character carried,
        BackpackSlotSnapshot carrierBackpackSnapshot,
        BackpackSlotSnapshot carriedBackpackSnapshot)
    {
        Plugin.Log.LogWarning($"Rolling back BuddyClimb backpack transfer for {carried.characterName}.");

        carrierBackpackSnapshot.Restore(carrier.player);
        carriedBackpackSnapshot.Restore(carried.player);
        carrier.refs.afflictions.UpdateWeight();
        carried.refs.afflictions.UpdateWeight();

        SyncInventory(carrier.player);
        SyncInventory(carried.player);

        // The failure path may have already detached a held backpack from carrier.
        // Refresh the native visual/equip state so the restored slot does not leave
        // a stale held-item visual or selected backpack slot behind.
        ClearHeldBackpackAfterTransfer(carrier);
    }

    private static void AssignBackpack(Player player, BackpackSlot source)
    {
        if (!CanSyncInventory(player))
        {
            throw new System.InvalidOperationException($"Cannot assign backpack because {player.name}'s inventory is unavailable.");
        }

        BackpackSlot destination = new BackpackSlot(BackpackSlotIndex)
        {
            backpackType = source.backpackType,
        };

        if (!source.IsEmpty())
        {
            destination.SetItem(source.prefab, source.data);
        }
        else
        {
            destination.EmptyOut();
        }

        player.backpackSlot = destination;
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

    private readonly struct BackpackSlotSnapshot
    {
        private readonly bool hasBackpack;
        private readonly BackpackSlot.BackpackType backpackType;
        private readonly Item? prefab;
        private readonly ItemInstanceData data;

        private BackpackSlotSnapshot(BackpackSlot backpackSlot)
        {
            hasBackpack = !backpackSlot.IsEmpty();
            backpackType = backpackSlot.backpackType;
            prefab = backpackSlot.prefab;
            data = backpackSlot.data;
        }

        internal static BackpackSlotSnapshot Capture(Player player)
        {
            return new BackpackSlotSnapshot(player.backpackSlot);
        }

        internal BackpackSlot RestoredSlot()
        {
            BackpackSlot backpackSlot = new BackpackSlot(BackpackSlotIndex)
            {
                backpackType = backpackType,
            };

            if (hasBackpack)
            {
                backpackSlot.SetItem(prefab, data);
            }
            else
            {
                backpackSlot.EmptyOut();
            }

            return backpackSlot;
        }

        internal void Restore(Player player)
        {
            if (!CanSyncInventory(player))
            {
                return;
            }

            player.backpackSlot = RestoredSlot();
        }
    }
}
