using HarmonyLib;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(CharacterCustomization))]
internal static class CharacterCustomizationPatch
{
    [HarmonyPatch("Start")]
    [HarmonyPrefix]
    private static void StartPrefix(CharacterCustomization __instance)
    {
        DummyPlayerSpawner.PrepareCustomizationStart(__instance);
    }

    [HarmonyPatch("Start")]
    [HarmonyPostfix]
    private static void StartPostfix(CharacterCustomization __instance)
    {
        DummyPlayerSpawner.FinalizeCustomizationStart(__instance);
    }
}
