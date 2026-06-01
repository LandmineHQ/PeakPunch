using HarmonyLib;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(Player))]
internal static class PlayerPatch
{
    [HarmonyPatch("Awake")]
    [HarmonyPrefix]
    private static bool AwakePrefix(Player __instance)
    {
        return !DummyPlayerSpawner.TryInitializeSyntheticPlayerAwake(__instance);
    }

    [HarmonyPatch(nameof(Player.character), MethodType.Getter)]
    [HarmonyPrefix]
    private static bool CharacterGetterPrefix(Player __instance, ref Character __result)
    {
        if (DummyPlayerSpawner.TryGetDummyCharacter(__instance, out Character character))
        {
            __result = character;
            return false;
        }

        return true;
    }
}
