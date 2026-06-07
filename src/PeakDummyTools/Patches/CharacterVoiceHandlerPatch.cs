using HarmonyLib;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(CharacterVoiceHandler))]
internal static class CharacterVoiceHandlerPatch
{
    [HarmonyPatch("PushToTalk")]
    [HarmonyPostfix]
    private static void PushToTalkPostfix(CharacterVoiceHandler __instance)
    {
        DummyControlVoiceDriver.EnforceTemporaryMute(__instance);
    }
}
