using BuddyClimb.Compatibility;
using HarmonyLib;

namespace BuddyClimb.Patches;

[HarmonyPatch]
internal static class CarrySpectatePatch
{
    private const float CarrySpectateZoomMax = 3f;

    private static float? defaultSpectateZoomMax;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        return !ModCompatibility.IsPiggybackLoaded;
    }

    [HarmonyPatch(typeof(MainCameraMovement), "LateUpdate")]
    [HarmonyPostfix]
    private static void LateUpdatePostfix(MainCameraMovement __instance)
    {
        if (!TryGetBuddyClimbCarrier(out Character carrier))
        {
            RestoreSpectateZoomMax(__instance);
            return;
        }

        if (__instance.isGodCam || __instance.isSpectating)
        {
            return;
        }

        MainCamera camera = __instance.cam;
        if (camera == null || camera.camOverride != null)
        {
            return;
        }

        MainCameraMovement.specCharacter = carrier;
        ApplySpectateZoomMax(__instance);
        __instance.Spectate();
        __instance.isSpectating = true;
    }

    [HarmonyPatch(typeof(MainCameraMovement), "HandleSpecSelection")]
    [HarmonyPrefix]
    private static bool HandleSpecSelectionPrefix(MainCameraMovement __instance, ref bool __result)
    {
        if (!TryGetBuddyClimbCarrier(out Character carrier))
        {
            RestoreSpectateZoomMax(__instance);
            return true;
        }

        MainCameraMovement.specCharacter = carrier;
        ApplySpectateZoomMax(__instance);
        __result = true;
        return false;
    }

    private static bool TryGetBuddyClimbCarrier(out Character carrier)
    {
        carrier = null!;

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

        carrier = localCharacter.data.carrier;
        return true;
    }

    private static void ApplySpectateZoomMax(MainCameraMovement cameraMovement)
    {
        defaultSpectateZoomMax ??= cameraMovement.spectateZoomMax;
        cameraMovement.spectateZoomMax = CarrySpectateZoomMax;
    }

    private static void RestoreSpectateZoomMax(MainCameraMovement cameraMovement)
    {
        if (!defaultSpectateZoomMax.HasValue)
        {
            return;
        }

        cameraMovement.spectateZoomMax = defaultSpectateZoomMax.Value;
        defaultSpectateZoomMax = null;
    }
}
