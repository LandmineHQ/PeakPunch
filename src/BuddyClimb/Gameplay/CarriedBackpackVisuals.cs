using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BuddyClimb.Gameplay;

internal static class CarriedBackpackVisuals
{
    private static readonly byte BackpackSlotIndex = (byte)Player.BACKPACKSLOTINDEX;
    private static readonly System.Reflection.MethodInfo HideRenderersMethod =
        AccessTools.Method(typeof(Item), "HideRenderers");

    private static readonly HashSet<int> ForcedVisibleItemCharacterViewIds = [];

    internal static void Update(CharacterBackpackHandler handler)
    {
        if (handler == null || handler.character == null || handler.character.photonView == null)
        {
            return;
        }

        Character character = handler.character;
        int viewId = character.photonView.ViewID;
        bool shouldShow = ShouldForceBackpackItemRender(character);

        if (shouldShow)
        {
            bool wasForcedVisible = ForcedVisibleItemCharacterViewIds.Contains(viewId);
            if (ShowBackpackItems(handler, refreshItems: !wasForcedVisible))
            {
                ForcedVisibleItemCharacterViewIds.Add(viewId);
            }

            return;
        }

        if (ForcedVisibleItemCharacterViewIds.Remove(viewId))
        {
            HideBackpackItems(handler);
        }
    }

    internal static void ShowIfCarriedLocalBackpackItem(Item item, BackpackReference backpackReference)
    {
        if (item == null || !backpackReference.IsOnMyBack())
        {
            return;
        }

        Character localCharacter = Character.localCharacter;
        if (!ShouldForceBackpackItemRender(localCharacter))
        {
            return;
        }

        ShowItem(item);
    }

    internal static void HideIfForcedVisible(Character character)
    {
        if (character == null || character.photonView == null)
        {
            return;
        }

        if (!ForcedVisibleItemCharacterViewIds.Remove(character.photonView.ViewID))
        {
            return;
        }

        CharacterBackpackHandler handler = character.GetComponent<CharacterBackpackHandler>();
        if (handler != null)
        {
            HideBackpackItems(handler);
        }
    }

    private static bool ShouldForceBackpackItemRender(Character character)
    {
        return character != null
            && character.IsLocal
            && character.data != null
            && character.data.isCarried
            && !character.data.dead
            && !character.data.fullyPassedOut
            && MainCameraMovement.IsSpectating
            && MainCameraMovement.specCharacter == character
            && HasOnBackBackpack(character)
            && Patches.CharacterCarryingPatch.IsBuddyClimbCarried(character);
    }

    private static bool ShowBackpackItems(CharacterBackpackHandler handler, bool refreshItems)
    {
        if (!HasOnBackBackpack(handler.character))
        {
            return false;
        }

        if (!refreshItems)
        {
            return true;
        }

        BackpackOnBackVisuals backpackVisuals = handler.backpackVisuals;
        if (backpackVisuals == null)
        {
            return false;
        }

        backpackVisuals.RefreshVisuals();
        SetSpawnedItemsVisible(backpackVisuals, visible: true);
        return true;
    }

    private static void HideBackpackItems(CharacterBackpackHandler handler)
    {
        SetSpawnedItemsVisible(handler.backpackVisuals, visible: false);
    }

    private static void SetSpawnedItemsVisible(BackpackOnBackVisuals backpackVisuals, bool visible)
    {
        if (backpackVisuals == null)
        {
            return;
        }

        BackpackData backpackData = backpackVisuals.GetBackpackData();
        if (backpackData?.itemSlots == null)
        {
            return;
        }

        for (byte slot = 0; slot < backpackData.itemSlots.Length; slot++)
        {
            ItemSlot itemSlot = backpackData.itemSlots[slot];
            if (itemSlot == null || itemSlot.IsEmpty())
            {
                continue;
            }

            if (!backpackVisuals.TryGetSpawnedItem(slot, out Item spawnedItem) || spawnedItem == null)
            {
                continue;
            }

            if (visible)
            {
                ShowItem(spawnedItem);
            }
            else
            {
                HideItem(spawnedItem);
            }
        }
    }

    private static bool HasOnBackBackpack(Character character)
    {
        return character?.player?.backpackSlot is { hasBackpack: true }
            && !IsBackpackSlotSelected(character);
    }

    private static bool IsBackpackSlotSelected(Character character)
    {
        CharacterItems characterItems = character.refs.items;
        return characterItems != null
            && characterItems.currentSelectedSlot.IsSome
            && characterItems.currentSelectedSlot.Value == BackpackSlotIndex;
    }

    private static void ShowItem(Item item)
    {
        foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            renderer.enabled = true;
        }
    }

    private static void HideItem(Item item)
    {
        HideRenderersMethod.Invoke(item, null);
    }
}
