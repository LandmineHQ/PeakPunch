using BuddyClimb.Debugging;
using HarmonyLib;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(Player))]
internal static class PlayerPatch
{
    [HarmonyPatch("Awake")]
    [HarmonyPrefix]
    private static bool AwakePrefix(Player __instance)
    {
        return !DebugPlayerSpawner.TryInitializeSyntheticPlayerAwake(__instance);
    }

    [HarmonyPatch(nameof(Player.character), MethodType.Getter)]
    [HarmonyPrefix]
    private static bool CharacterGetterPrefix(Player __instance, ref Character __result)
    {
        if (DebugPlayerSpawner.TryGetDebugCharacter(__instance, out Character character))
        {
            __result = character;
            return false;
        }

        return true;
    }
}
