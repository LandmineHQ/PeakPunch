using HarmonyLib;
using Photon.Pun;
using BuddyClimb.Debugging;
using BuddyClimb.Localization;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(CharacterInteractible))]
internal static class CharacterInteractiblePatch
{
    [HarmonyPatch(nameof(CharacterInteractible.GetInteractionText))]
    [HarmonyPostfix]
    private static void GetInteractionTextPatch(ref string __result, CharacterInteractible __instance)
    {
        if (__result != string.Empty)
        {
            return;
        }

        if (CanBeClimbed(__instance.character))
        {
            __result = BuddyClimbLocalization.Get(BuddyClimbTextKey.ClimbOnTeammate);
        }
    }

    [HarmonyPatch(nameof(CharacterInteractible.Interact))]
    [HarmonyPostfix]
    private static void InteractPatch(CharacterInteractible __instance, ref Character interactor)
    {
        if (__instance.CarriedByLocalCharacter() || (!__instance.IsCannibal() && __instance.CanBeCarried()))
        {
            return;
        }

        if (CanBeClimbed(__instance.character) && CanClimb(interactor))
        {
            Photon.Realtime.Player passOutRpcTarget = GetPassOutRpcTarget(__instance.character);
            interactor.photonView.RPC("RPCA_PassOut", passOutRpcTarget);
            __instance.character.refs.carriying.StartCarry(interactor);
        }
    }

    [HarmonyPatch(nameof(CharacterInteractible.IsInteractible))]
    [HarmonyPostfix]
    private static void IsInteractiblePatch(CharacterInteractible __instance, ref bool __result, ref Character interactor)
    {
        if (__result)
        {
            return;
        }

        if (CanBeClimbed(__instance.character) && CanClimb(interactor))
        {
            __result = true;
        }
    }

    [HarmonyPatch(nameof(CharacterInteractible.IsPrimaryInteractible))]
    [HarmonyPostfix]
    private static void IsPrimaryInteractiblePatch(CharacterInteractible __instance, ref bool __result, ref Character interactor)
    {
        if (__result)
        {
            return;
        }

        if (CanBeClimbed(__instance.character) && CanClimb(interactor))
        {
            __result = true;
        }
    }

    [HarmonyPatch(typeof(CharacterCarrying), nameof(CharacterCarrying.RPCA_Drop))]
    [HarmonyPrefix]
    private static bool RPCA_DropPrefix(PhotonView targetView)
    {
        if (targetView == null || !targetView.IsMine)
        {
            return true;
        }

        Character character = targetView.GetComponent<Character>();
        Character? carrier = character?.data.carrier;
        if (character != null && carrier != null && !character.data.fullyPassedOut)
        {
            targetView.RPC("RPCA_UnPassOut", carrier.photonView.Owner);
        }

        return true;
    }

    private static bool CanBeClimbed(Character character)
    {
        bool isDebugSpawnedPlayer = DebugPlayerSpawner.IsDebugSpawnedPlayer(character);
        if (character.isBot && !isDebugSpawnedPlayer)
        {
            return false;
        }

        if (character.IsLocal)
        {
            return false;
        }

        if (character.data.dead)
        {
            return false;
        }

        if (!isDebugSpawnedPlayer && character.player.backpackSlot.hasBackpack)
        {
            return false;
        }

        if (IsCharacterDoingIllegalCarryActions(character))
        {
            return false;
        }

        if (character.data.carriedPlayer)
        {
            return false;
        }

        if (character.data.carrier)
        {
            return false;
        }

        if (character.data.currentItem && character.data.currentItem.canUseOnFriend)
        {
            return false;
        }

        if (character.refs.customization.isCannibalizable)
        {
            return false;
        }

        return true;
    }

    private static Photon.Realtime.Player GetPassOutRpcTarget(Character character)
    {
        if (DebugPlayerSpawner.IsDebugSpawnedPlayer(character))
        {
            return character.photonView.Owner;
        }

        return character.player.photonView.Owner;
    }

    private static bool CanClimb(Character character)
    {
        if (character.refs.interactible.CanBeCarried())
        {
            return true;
        }

        if (!character.IsLocal)
        {
            return false;
        }

        if (character.data.dead)
        {
            return false;
        }

        if (character.data.currentItem)
        {
            return false;
        }

        if (character.data.carrier)
        {
            return false;
        }

        if (character.refs.customization.isCannibalizable)
        {
            return false;
        }

        return true;
    }

    private static bool IsCharacterDoingIllegalCarryActions(Character character)
    {
        return character.data.isSprinting
            || character.data.isJumping
            || character.data.isClimbingAnything
            || character.data.isCrouching
            || character.data.isReaching;
    }
}
