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
}
