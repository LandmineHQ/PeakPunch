using HarmonyLib;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(CharacterInteractible))]
internal static class CharacterInteractiblePatch
{
    [HarmonyPatch(nameof(CharacterInteractible.IsInteractible))]
    [HarmonyPostfix]
    private static void IsInteractiblePostfix(CharacterInteractible __instance, ref bool __result, Character interactor)
    {
        if (__result || interactor != Character.localCharacter)
        {
            return;
        }

        if (DummyControlSwitcher.CanShowSwitchPrompt(__instance.character))
        {
            __result = true;
        }
    }
}
