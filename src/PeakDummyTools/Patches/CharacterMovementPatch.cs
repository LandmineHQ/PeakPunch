using HarmonyLib;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(CharacterMovement))]
internal static class CharacterMovementPatch
{
    [HarmonyPatch("SetMovementState")]
    [HarmonyPrefix]
    private static bool SetMovementStatePrefix(CharacterMovement __instance)
    {
        return !DummyControlMovementStateDriver.TryHandleSetMovementState(__instance);
    }
}
