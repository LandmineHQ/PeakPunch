using System.Collections.Generic;
using BuddyClimb.Gameplay;
using HarmonyLib;
using Photon.Pun;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(CharacterCarrying))]
internal static class CharacterCarryingPatch
{
    private static readonly HashSet<int> BuddyClimbCarriedViewIds = [];

    internal static bool IsBuddyClimbCarried(Character character)
    {
        return character?.photonView != null
            && BuddyClimbCarriedViewIds.Contains(character.photonView.ViewID);
    }

    [HarmonyPatch("Update")]
    [HarmonyPrefix]
    private static bool UpdatePrefix(CharacterCarrying __instance)
    {
        Character character = __instance.character ?? __instance.GetComponent<Character>();
        if (character == null || character.photonView == null)
        {
            return true;
        }

        Character carriedPlayer = character.data.carriedPlayer;
        if (carriedPlayer == null || !IsBuddyClimbCarried(carriedPlayer))
        {
            return true;
        }

        if ((carriedPlayer.data.dead || character.data.fullyPassedOut || character.data.dead)
            && character.refs.view.IsMine)
        {
            __instance.Drop(carriedPlayer);
        }

        return false;
    }

    [HarmonyPatch(nameof(CharacterCarrying.RPCA_Drop))]
    [HarmonyPrefix]
    private static bool RPCA_DropPrefix(PhotonView targetView)
    {
        if (targetView == null)
        {
            return true;
        }

        BuddyClimbCarriedViewIds.Remove(targetView.ViewID);

        return true;
    }

    [HarmonyPatch(nameof(CharacterCarrying.RPCA_Drop))]
    [HarmonyPostfix]
    private static void RPCA_DropPostfix(PhotonView targetView)
    {
        if (targetView != null)
        {
            Character carriedCharacter = targetView.GetComponent<Character>();
            CarryInteractionProxy.Disable(carriedCharacter);
            CarriedBackpackVisuals.HideIfForcedVisible(carriedCharacter);
        }
    }

    [HarmonyPatch(nameof(CharacterCarrying.RPCA_StartCarry))]
    [HarmonyPrefix]
    private static bool RPCA_StartCarryPrefix(CharacterCarrying __instance, PhotonView targetView)
    {
        if (targetView == null)
        {
            return false;
        }

        Character carrierCharacter = __instance.character ?? __instance.GetComponent<Character>();
        if (carrierCharacter == null)
        {
            return false;
        }

        Character carriedCharacter = targetView.GetComponent<Character>();
        if (carriedCharacter == null)
        {
            return false;
        }

        if (carriedCharacter.data.fullyPassedOut || carriedCharacter.data.dead)
        {
            return true;
        }

        if (carrierCharacter.data.carriedPlayer != null)
        {
            __instance.Drop(carrierCharacter.data.carriedPlayer);
            return false;
        }

        carriedCharacter.refs.carriying.ToggleCarryPhysics(true);
        carriedCharacter.data.isCarried = true;
        carrierCharacter.data.carriedPlayer = carriedCharacter;
        carriedCharacter.data.carrier = carrierCharacter;
        BuddyClimbCarriedViewIds.Add(carriedCharacter.photonView.ViewID);

        foreach (Character playerCharacter in PlayerHandler.GetAllPlayerCharacters())
        {
            playerCharacter.refs.afflictions.UpdateWeight();
        }

        return false;
    }

    [HarmonyPatch(nameof(CharacterCarrying.RPCA_StartCarry))]
    [HarmonyPostfix]
    private static void RPCA_StartCarryPostfix(PhotonView targetView)
    {
        if (targetView != null
            && targetView.GetComponent<Character>() is Character carriedCharacter
            && carriedCharacter.data.isCarried)
        {
            CarryInteractionProxy.Enable(carriedCharacter);
            if (carriedCharacter.IsLocal)
            {
                CarriedBackpackVisuals.Update(carriedCharacter.GetComponent<CharacterBackpackHandler>());
            }
        }
    }
}
