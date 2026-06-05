using BuddyClimb.Gameplay;
using HarmonyLib;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(CharacterBackpackHandler))]
internal static class CharacterBackpackHandlerPatch
{
    [HarmonyPatch("LateUpdate")]
    [HarmonyPostfix]
    private static void LateUpdatePostfix(CharacterBackpackHandler __instance)
    {
        CarriedBackpackVisuals.Update(__instance);
    }
}
