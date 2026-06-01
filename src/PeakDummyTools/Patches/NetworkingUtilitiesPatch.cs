using HarmonyLib;
using PeakDummyTools.DummyPlayers;
using Peak.Network;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(NetworkingUtilities))]
internal static class NetworkingUtilitiesPatch
{
    [HarmonyPatch(nameof(NetworkingUtilities.GetUserId))]
    [HarmonyPrefix]
    private static bool GetUserIdPrefix(Player self, ref string __result)
    {
        if (DummyPlayerSpawner.TryGetDummyPlayerUserId(self, out string userId))
        {
            __result = userId;
            return false;
        }

        return true;
    }

    [HarmonyPatch(nameof(NetworkingUtilities.GetActorNumber))]
    [HarmonyPrefix]
    private static bool GetActorNumberPrefix(Player self, ref int __result)
    {
        if (DummyPlayerSpawner.TryGetDummyPlayerActorNumber(self, out int actorNumber))
        {
            __result = actorNumber;
            return false;
        }

        return true;
    }
}
