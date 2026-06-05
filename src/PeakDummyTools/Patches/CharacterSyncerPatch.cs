using HarmonyLib;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(CharacterSyncer))]
internal static class CharacterSyncerPatch
{
    [HarmonyPatch("OnDataReceived")]
    [HarmonyPostfix]
    private static void OnDataReceivedPostfix(CharacterSyncer __instance, CharacterSyncData data)
    {
        DummyControlLookSyncDriver.HandleRemoteSync(__instance, data);
    }
}
