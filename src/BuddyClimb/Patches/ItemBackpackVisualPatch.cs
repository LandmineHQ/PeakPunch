using BuddyClimb.Gameplay;
using HarmonyLib;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(Item))]
internal static class ItemBackpackVisualPatch
{
    [HarmonyPatch(nameof(Item.PutInBackpackRPC))]
    [HarmonyPostfix]
    private static void PutInBackpackRPCPostfix(Item __instance, BackpackReference backpackReference)
    {
        CarriedBackpackVisuals.ShowIfCarriedLocalBackpackItem(__instance, backpackReference);
    }
}
