using HarmonyLib;
using PeakDummyTools.DummyPlayers;
using Zorro.Core;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(CharacterItems))]
internal static class CharacterItemsPatch
{
    [HarmonyPatch(nameof(CharacterItems.EquipSlot))]
    [HarmonyPrefix]
    private static bool EquipSlotPrefix(CharacterItems __instance, Optionable<byte> slotID)
    {
        return !DummyControlItemRpcDriver.TryHandleEquipSlot(__instance, slotID);
    }

    [HarmonyPatch(nameof(CharacterItems.EquipSlotRpc))]
    [HarmonyPrefix]
    private static void EquipSlotRpcPrefix(CharacterItems __instance, int slotID)
    {
        DummyControlItemSelectionSyncDriver.ApplyRemoteEquipSelection(__instance, slotID);
    }

    [HarmonyPatch(nameof(CharacterItems.EquipSlotRpc))]
    [HarmonyPostfix]
    private static void EquipSlotRpcPostfix(CharacterItems __instance)
    {
        DummyControlItemSelectionSyncDriver.RefreshControlledSelectionUi(__instance);
    }
}
