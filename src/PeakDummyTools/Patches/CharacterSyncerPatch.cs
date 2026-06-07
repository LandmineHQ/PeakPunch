using HarmonyLib;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(CharacterSyncer))]
internal static class CharacterSyncerPatch
{
    [HarmonyPatch("OnDataReceived")]
    [HarmonyPrefix]
    private static void OnDataReceivedPrefix(CharacterSyncer __instance, out bool __state)
    {
        __state = DummyControlLookSyncDriver.IsLocalInputActiveBeforeRemoteSync(__instance);
    }

    [HarmonyPatch("OnDataReceived")]
    [HarmonyPostfix]
    private static void OnDataReceivedPostfix(CharacterSyncer __instance, CharacterSyncData data, bool __state)
    {
        DummyControlLookSyncDriver.HandleRemoteSync(__instance, data, __state);
    }
}
