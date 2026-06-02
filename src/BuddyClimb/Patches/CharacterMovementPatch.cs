using BuddyClimb.Gameplay;
using HarmonyLib;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(CharacterMovement))]
internal static class CharacterMovementPatch
{
    [HarmonyPatch(nameof(CharacterMovement.TryToJump))]
    [HarmonyPrefix]
    private static bool TryToJumpPrefix(CharacterMovement __instance)
    {
        return !CarriedPlayerDropper.HandleJumpAttempt(__instance.character);
    }
}
