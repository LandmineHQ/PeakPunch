using BuddyClimb.Gameplay;
using HarmonyLib;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(Player))]
internal static class PlayerInventoryPatch
{
    [HarmonyPatch(nameof(Player.SyncInventoryRPC))]
    [HarmonyPrefix]
    private static bool SyncInventoryRPCPrefix(Player __instance, byte[] data, bool forceSync)
    {
        return !BackpackCarryTransfer.ShouldSuppressStaleBackpackEmptySync(__instance, data, forceSync);
    }
}
