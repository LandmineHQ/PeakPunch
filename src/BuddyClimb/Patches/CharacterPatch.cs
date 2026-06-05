using BuddyClimb.Gameplay;
using HarmonyLib;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(Character))]
internal static class CharacterPatch
{
    [HarmonyPatch(nameof(Character.Start))]
    [HarmonyPostfix]
    private static void StartPostfix(Character __instance)
    {
        BackpackTransferRpc.Ensure(__instance);
    }
}
