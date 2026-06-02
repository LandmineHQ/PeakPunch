using HarmonyLib;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(Character))]
internal static class CharacterPatch
{
    [HarmonyPatch("Awake")]
    [HarmonyPrefix]
    private static void AwakePrefix(Character __instance)
    {
        DummyPlayerSpawner.PrepareCharacterAwake(__instance);
    }

    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    private static void AwakePostfix(Character __instance)
    {
        DummyPlayerSpawner.FinalizeCharacterAwake(__instance);
    }

    [HarmonyPatch("Start")]
    [HarmonyPostfix]
    private static void StartPostfix(Character __instance)
    {
        DummyPlayerSpawner.FinalizeCharacterStart(__instance);
    }

    [HarmonyPatch("OnDestroy")]
    [HarmonyPostfix]
    private static void OnDestroyPostfix(Character __instance)
    {
        DummyControlSwitcher.HandleCharacterRemoved(__instance);
        DummyPlayerSpawner.RemoveDummyPlayer(__instance);
    }

    [HarmonyPatch(nameof(Character.characterName), MethodType.Getter)]
    [HarmonyPostfix]
    private static void CharacterNameGetterPostfix(Character __instance, ref string __result)
    {
        if (DummyPlayerSpawner.TryGetDummyPlayerName(__instance, out string name))
        {
            __result = name;
        }
    }

    [HarmonyPatch(nameof(Character.player), MethodType.Getter)]
    [HarmonyPrefix]
    private static bool PlayerGetterPrefix(Character __instance, ref Player __result)
    {
        if (DummyPlayerSpawner.TryGetDummyPlayer(__instance, out Player player))
        {
            __result = player;
            return false;
        }

        return true;
    }
}
