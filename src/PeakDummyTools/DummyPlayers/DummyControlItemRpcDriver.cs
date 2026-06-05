using Photon.Pun;
using UnityEngine;
using Zorro.Core;

namespace PeakDummyTools.DummyPlayers;

internal static class DummyControlItemRpcDriver
{
    private const string ItemPrefabRoot = "0_Items/";

    internal static bool TryHandleEquipSlot(CharacterItems characterItems, Optionable<byte> slotId)
    {
        if (characterItems == null
            || characterItems.character == null
            || characterItems.photonView == null
            || !DummyControlSwitcher.IsControllingTarget(characterItems.character))
        {
            return false;
        }

        characterItems.lastEquippedSlotTime = Time.time;
        if (slotId.IsSome)
        {
            characterItems.lastSelectedSlot = slotId;
        }

        CancelCurrentItemUse(characterItems);
        if (!TryDropUnpocketableCurrentItem(characterItems))
        {
            return true;
        }

        if (!slotId.IsSome)
        {
            if (characterItems.currentSelectedSlot.IsSome)
            {
                characterItems.lastSelectedSlot = characterItems.currentSelectedSlot;
            }

            characterItems.currentSelectedSlot = Optionable<byte>.None;
            SendEquipSlotRpc(characterItems, -1, -1);
            return true;
        }

        byte slot = slotId.Value;
        characterItems.currentSelectedSlot = slotId;

        int objectViewId = -1;
        ItemSlot itemSlot = characterItems.character.player.GetItemSlot(slot);
        if (itemSlot != null && !itemSlot.IsEmpty())
        {
            if (!TryInstantiateSlotItem(characterItems.character, itemSlot, out objectViewId))
            {
                return true;
            }
        }

        SendEquipSlotRpc(characterItems, slot, objectViewId);
        return true;
    }

    private static void CancelCurrentItemUse(CharacterItems characterItems)
    {
        Item currentItem = characterItems.character.data.currentItem;
        if (currentItem == null)
        {
            return;
        }

        currentItem.CancelUsePrimary();
        currentItem.CancelUseSecondary();
    }

    private static bool TryDropUnpocketableCurrentItem(CharacterItems characterItems)
    {
        Item currentItem = characterItems.character.data.currentItem;
        if (currentItem == null || currentItem.UIData.canPocket)
        {
            return true;
        }

        if (!characterItems.currentSelectedSlot.IsSome)
        {
            return true;
        }

        ItemSlot currentSlot = characterItems.character.player.GetItemSlot(characterItems.currentSelectedSlot.Value);
        if (currentSlot == null)
        {
            Plugin.Log.LogWarning("Unable to drop the currently held item before switching because the current slot is unavailable.");
            return false;
        }

        Vector3 spawnPosition = currentItem.transform.position + Vector3.down * 0.2f;
        Vector3 velocity = currentItem.rig != null ? currentItem.rig.linearVelocity : Vector3.zero;
        Quaternion rotation = currentItem.transform.rotation;

        characterItems.photonView.RPC(
            nameof(CharacterItems.DropItemRpc),
            RpcTarget.All,
            characterItems.throwChargeLevel,
            characterItems.currentSelectedSlot.Value,
            spawnPosition,
            velocity,
            rotation,
            currentSlot.data,
            false);

        characterItems.throwChargeLevel = 0f;
        return true;
    }

    private static bool TryInstantiateSlotItem(Character character, ItemSlot itemSlot, out int objectViewId)
    {
        objectViewId = -1;
        Bodypart torso = character.GetBodypart(BodypartType.Torso);
        if (torso == null)
        {
            Plugin.Log.LogWarning($"Unable to equip {character.characterName}'s slot because the torso bodypart is unavailable.");
            return false;
        }

        Transform spawnTransform = torso.transform;
        GameObject itemObject = PhotonNetwork.Instantiate(
            ItemPrefabRoot + itemSlot.GetPrefabName(),
            spawnTransform.position + spawnTransform.forward * 0.6f,
            Quaternion.identity,
            0,
            null);

        if (itemObject == null || !itemObject.TryGetComponent(out PhotonView itemView))
        {
            Plugin.Log.LogWarning($"Unable to equip {character.characterName}'s slot because the item PhotonView could not be created.");
            return false;
        }

        itemView.RPC(nameof(Item.SetItemInstanceDataRPC), RpcTarget.All, itemSlot.data);
        objectViewId = itemView.ViewID;
        return true;
    }

    private static void SendEquipSlotRpc(CharacterItems characterItems, int slotId, int objectViewId)
    {
        characterItems.photonView.RPC(
            nameof(CharacterItems.EquipSlotRpc),
            RpcTarget.All,
            slotId,
            objectViewId);
    }
}
