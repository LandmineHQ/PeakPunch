using BuddyClimb.Debugging;
using HarmonyLib;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(Character))]
internal static class CharacterPatch
{
    [HarmonyPatch("Awake")]
    [HarmonyPrefix]
    private static void AwakePrefix(Character __instance)
    {
        DebugPlayerSpawner.PrepareCharacterAwake(__instance);
    }

    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    private static void AwakePostfix(Character __instance)
    {
        DebugPlayerSpawner.FinalizeCharacterAwake(__instance);
    }

    [HarmonyPatch("Start")]
    [HarmonyPostfix]
    private static void StartPostfix(Character __instance)
    {
        DebugPlayerSpawner.FinalizeCharacterStart(__instance);
    }

    [HarmonyPatch("OnDestroy")]
    [HarmonyPostfix]
    private static void OnDestroyPostfix(Character __instance)
    {
        DebugPlayerSpawner.RemoveDebugPlayer(__instance);
    }

    [HarmonyPatch(nameof(Character.characterName), MethodType.Getter)]
    [HarmonyPostfix]
    private static void CharacterNameGetterPostfix(Character __instance, ref string __result)
    {
        if (DebugPlayerSpawner.TryGetDebugPlayerName(__instance, out string name))
        {
            __result = name;
        }
    }

    [HarmonyPatch(nameof(Character.player), MethodType.Getter)]
    [HarmonyPrefix]
    private static bool PlayerGetterPrefix(Character __instance, ref Player __result)
    {
        if (DebugPlayerSpawner.TryGetDebugPlayer(__instance, out Player player))
        {
            __result = player;
            return false;
        }

        return true;
    }
}
