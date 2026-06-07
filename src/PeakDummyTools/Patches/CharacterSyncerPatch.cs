using HarmonyLib;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(CharacterSyncer))]
internal static class CharacterSyncerPatch
{
    [HarmonyPatch("OnDataReceived")]
    [HarmonyPrefix]
    private static bool OnDataReceivedPrefix(
        CharacterSyncer __instance,
        out DummyControlLookSyncDriver.LocalInputSnapshot? __state)
    {
        __state = DummyControlLookSyncDriver.CaptureLocalInputBeforeRemoteSync(__instance);
        return __state == null;
    }

    [HarmonyPatch("OnDataReceived")]
    [HarmonyPostfix]
    private static void OnDataReceivedPostfix(
        CharacterSyncer __instance,
        CharacterSyncData data,
        DummyControlLookSyncDriver.LocalInputSnapshot? __state)
    {
        DummyControlLookSyncDriver.HandleRemoteSync(__instance, data, __state);
    }
}
