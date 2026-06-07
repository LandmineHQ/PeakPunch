using System.Reflection;
using HarmonyLib;
using Zorro.Core;

namespace PeakDummyTools.DummyPlayers;

internal static class DummyControlItemSelectionSyncDriver
{
    private static readonly MethodInfo? GuiOnSlotEquippedMethod = AccessTools.Method(
        typeof(GUIManager),
        "OnSlotEquipped");

    internal static bool ApplyRemoteEquipSelection(CharacterItems? characterItems, int slotId)
    {
        if (characterItems == null || !IsControlledTarget(characterItems))
        {
            return false;
        }

        if (slotId < 0)
        {
            if (characterItems.currentSelectedSlot.IsSome)
            {
                characterItems.lastSelectedSlot = characterItems.currentSelectedSlot;
            }

            characterItems.currentSelectedSlot = Optionable<byte>.None;
            return true;
        }

        if (slotId > byte.MaxValue)
        {
            return false;
        }

        characterItems.currentSelectedSlot = Optionable<byte>.Some((byte)slotId);
        return true;
    }

    internal static void RefreshControlledSelectionUi(CharacterItems? characterItems)
    {
        if (!IsControlledTarget(characterItems))
        {
            return;
        }

        GUIManager? guiManager = GUIManager.instance;
        if (guiManager == null || GuiOnSlotEquippedMethod == null)
        {
            return;
        }

        if (guiManager.character == characterItems!.character)
        {
            return;
        }

        GuiOnSlotEquippedMethod.Invoke(guiManager, []);
    }

    private static bool IsControlledTarget(CharacterItems? characterItems)
    {
        return characterItems != null
            && characterItems.character != null
            && DummyControlSwitcher.IsControllingTarget(characterItems.character);
    }
}
