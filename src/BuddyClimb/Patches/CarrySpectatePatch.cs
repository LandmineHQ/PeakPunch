using BuddyClimb.Compatibility;
using HarmonyLib;

namespace BuddyClimb.Patches;

[HarmonyPatch]
internal static class CarrySpectatePatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        return !ModCompatibility.IsPiggybackLoaded;
    }

    [HarmonyPatch(typeof(MainCameraMovement), "LateUpdate")]
    [HarmonyPostfix]
    private static void LateUpdatePostfix(MainCameraMovement __instance)
    {
        if (!TryGetBuddyClimbSpectateTarget(out Character spectateTarget)) return;

        if (__instance.isGodCam || __instance.isSpectating)
        {
            return;
        }

        MainCamera camera = __instance.cam;
        if (camera == null || camera.camOverride != null)
        {
            return;
        }

        MainCameraMovement.specCharacter = spectateTarget;
        __instance.Spectate();
        __instance.isSpectating = true;
    }

    [HarmonyPatch(typeof(MainCameraMovement), "HandleSpecSelection")]
    [HarmonyPrefix]
    private static bool HandleSpecSelectionPrefix(MainCameraMovement __instance, ref bool __result)
    {
        if (!TryGetBuddyClimbSpectateTarget(out Character spectateTarget)) return true;

        MainCameraMovement.specCharacter = spectateTarget;
        __result = true;
        return false;
    }

    private static bool TryGetBuddyClimbSpectateTarget(out Character spectateTarget)
    {
        spectateTarget = null!;

        Character localCharacter = Character.localCharacter;
        if (localCharacter == null
            || localCharacter.data == null
            || !localCharacter.data.isCarried
            || localCharacter.data.carrier == null
            || localCharacter.data.dead
            || localCharacter.data.fullyPassedOut
            || !CharacterCarryingPatch.IsBuddyClimbCarried(localCharacter))
        {
            return false;
        }

        spectateTarget = localCharacter;
        return true;
    }
}
