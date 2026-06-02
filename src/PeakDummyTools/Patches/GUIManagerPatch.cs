using HarmonyLib;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(GUIManager))]
internal static class GUIManagerPatch
{
    [HarmonyPatch("LateUpdate")]
    [HarmonyPostfix]
    private static void LateUpdatePostfix()
    {
        DummySwitchPromptUi.Update();
    }
}
