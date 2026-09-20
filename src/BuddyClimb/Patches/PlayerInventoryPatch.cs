using BuddyClimb.Gameplay;
using HarmonyLib;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(Player))]
internal static class PlayerInventoryPatch
{
    [HarmonyPatch(nameof(Player.SyncInventoryRPC))]
    [HarmonyPostfix]
    private static void SyncInventoryRPCPostfix(Player __instance, byte[] data, bool forceSync)
    {
        BackpackCarryTransfer.HandleInventorySync(__instance, data, forceSync);
    }
}
