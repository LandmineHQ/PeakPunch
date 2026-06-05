using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BuddyClimb.Gameplay;

internal static class CarriedBackpackVisuals
{
    private static readonly System.Reflection.MethodInfo HideRenderersMethod =
        AccessTools.Method(typeof(Item), "HideRenderers");

    private static readonly HashSet<int> ForcedVisibleCharacterViewIds = [];

    internal static void Update(CharacterBackpackHandler handler)
    {
        if (handler == null || handler.character == null || handler.character.photonView == null)
        {
            return;
        }

        Character character = handler.character;
        int viewId = character.photonView.ViewID;
        bool shouldShow = ShouldShowCarriedBackpack(character);

        if (shouldShow)
        {
            bool wasForcedVisible = ForcedVisibleCharacterViewIds.Contains(viewId);
            if (Show(handler, refreshItems: !wasForcedVisible))
            {
                ForcedVisibleCharacterViewIds.Add(viewId);
            }

            return;
        }

        if (ForcedVisibleCharacterViewIds.Remove(viewId))
        {
            Hide(handler);
        }
    }

    internal static void ShowIfCarriedLocalBackpackItem(Item item, BackpackReference backpackReference)
    {
        if (item == null || !backpackReference.IsOnMyBack())
        {
            return;
        }

        Character localCharacter = Character.localCharacter;
        if (!ShouldShowCarriedBackpack(localCharacter))
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

        if (!ForcedVisibleCharacterViewIds.Remove(character.photonView.ViewID))
        {
            return;
        }

        CharacterBackpackHandler handler = character.GetComponent<CharacterBackpackHandler>();
        if (handler != null)
        {
            Hide(handler);
        }
    }

    private static bool ShouldShowCarriedBackpack(Character character)
    {
        return character != null
            && character.IsLocal
            && character.data != null
            && character.data.isCarried
            && !character.data.dead
            && !character.data.fullyPassedOut
            && Patches.CharacterCarryingPatch.IsBuddyClimbCarried(character);
    }

    private static bool Show(CharacterBackpackHandler handler, bool refreshItems)
    {
        if (!HasVisibleBackpackSlot(handler.character))
        {
            return false;
        }

        handler.backpack?.SetActive(true);
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

    private static void Hide(CharacterBackpackHandler handler)
    {
        SetSpawnedItemsVisible(handler.backpackVisuals, visible: false);

        if (handler.character != null
            && handler.character.photonView != null
            && handler.character.photonView.IsMine
            && !MainCameraMovement.IsSpectating)
        {
            handler.backpack?.SetActive(false);
        }
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

    private static bool HasVisibleBackpackSlot(Character character)
    {
        return character?.player?.backpackSlot is { hasBackpack: true };
    }

    private static void ShowItem(Item item)
    {
        item.gameObject.SetActive(true);

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
