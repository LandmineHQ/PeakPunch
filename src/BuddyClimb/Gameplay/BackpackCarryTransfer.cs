using BuddyClimb.Configuration;
using Photon.Pun;
using UnityEngine;
using Zorro.Core.Serizalization;

namespace BuddyClimb.Gameplay;

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

    internal static bool TryTransferCarrierBackpack(Character carrier, Character carried)
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

        if (HasBackpack(carried) && !TryDropBackpack(carried))
        {
            return false;
        }

        BackpackSlot carrierBackpack = carrierPlayer.backpackSlot;
        carriedPlayer.backpackSlot = carrierBackpack;
        carrierPlayer.backpackSlot = new BackpackSlot(BackpackSlotIndex);

        SyncInventory(carriedPlayer);
        SyncInventory(carrierPlayer);

        return true;
    }

    private static bool HasBackpack(Character character)
    {
        return character.player?.backpackSlot is { hasBackpack: true };
    }

    private static bool TryDropBackpack(Character character)
    {
        CharacterItems characterItems = character.refs.items;
        if (characterItems == null || characterItems.photonView == null)
        {
            Plugin.Log.LogWarning($"Unable to drop {character.characterName}'s backpack because CharacterItems is unavailable.");
            return false;
        }

        characterItems.photonView.RPC(
            nameof(CharacterItems.DropItemFromSlotRPC),
            RpcTarget.All,
            BackpackSlotIndex,
            GetBackpackDropPosition(character));

        return true;
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
}
