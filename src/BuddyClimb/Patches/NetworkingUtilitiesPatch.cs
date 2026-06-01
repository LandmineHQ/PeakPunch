using BuddyClimb.Debugging;
using HarmonyLib;
using Peak.Network;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(NetworkingUtilities))]
internal static class NetworkingUtilitiesPatch
{
    [HarmonyPatch(nameof(NetworkingUtilities.GetUserId))]
    [HarmonyPrefix]
    private static bool GetUserIdPrefix(Player self, ref string __result)
    {
        if (DebugPlayerSpawner.TryGetDebugPlayerUserId(self, out string userId))
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
        if (DebugPlayerSpawner.TryGetDebugPlayerActorNumber(self, out int actorNumber))
        {
            __result = actorNumber;
            return false;
        }

        return true;
    }
}
