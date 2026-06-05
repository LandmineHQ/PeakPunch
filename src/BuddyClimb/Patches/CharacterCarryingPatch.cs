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
            BuddyClimbDiagnostics.LogCarry($"CharacterCarrying.Update dropping BuddyClimb carried player because carry state became invalid: {BuddyClimbDiagnostics.DescribeViews(character, carriedPlayer)}");
            __instance.Drop(carriedPlayer);
        }

        return false;
    }

    [HarmonyPatch(nameof(CharacterCarrying.RPCA_Drop))]
    [HarmonyPrefix]
    private static bool RPCA_DropPrefix(PhotonView targetView)
    {
        BuddyClimbDiagnostics.LogCarry($"RPCA_Drop prefix received targetView={(targetView != null ? targetView.ViewID : -1)}");
        if (targetView == null)
        {
            return true;
        }

        Character carriedCharacter = targetView.GetComponent<Character>();
        if (carriedCharacter != null && IsBuddyClimbCarried(carriedCharacter))
        {
            BuddyClimbRemotePassOutSync.RestoreRemoteCarryDrop(carriedCharacter);
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
            BuddyClimbDiagnostics.LogCarry($"RPCA_Drop postfix target: {BuddyClimbDiagnostics.Describe(carriedCharacter)}");
            CarryInteractionProxy.Disable(carriedCharacter);
            CarriedBackpackVisuals.HideIfForcedVisible(carriedCharacter);
        }
    }

    [HarmonyPatch(nameof(CharacterCarrying.RPCA_StartCarry))]
    [HarmonyPrefix]
    private static bool RPCA_StartCarryPrefix(CharacterCarrying __instance, PhotonView targetView)
    {
        Character carrierCharacterForLog = __instance.character ?? __instance.GetComponent<Character>();
        Character? carriedCharacterForLog = targetView != null ? targetView.GetComponent<Character>() : null;
        BuddyClimbDiagnostics.LogCarry(
            $"RPCA_StartCarry prefix received targetView={(targetView != null ? targetView.ViewID : -1)}: {BuddyClimbDiagnostics.DescribeViews(carrierCharacterForLog, carriedCharacterForLog)}");

        if (targetView == null)
        {
            BuddyClimbDiagnostics.LogCarry("RPCA_StartCarry prefix returning false because targetView is null.");
            return false;
        }

        Character? carrierCharacter = carrierCharacterForLog;
        if (carrierCharacter == null)
        {
            BuddyClimbDiagnostics.LogCarry("RPCA_StartCarry prefix returning false because carrierCharacter is null.");
            return false;
        }

        Character? carriedCharacter = carriedCharacterForLog;
        if (carriedCharacter == null)
        {
            BuddyClimbDiagnostics.LogCarry("RPCA_StartCarry prefix returning false because carriedCharacter is null.");
            return false;
        }

        if (carriedCharacter.data.fullyPassedOut || carriedCharacter.data.dead)
        {
            BuddyClimbDiagnostics.LogCarry($"RPCA_StartCarry prefix allowing vanilla path for unconscious/dead carried character: {BuddyClimbDiagnostics.Describe(carriedCharacter)}");
            return true;
        }

        Character existingCarriedPlayer = carrierCharacter.data.carriedPlayer;
        if (existingCarriedPlayer == carriedCharacter)
        {
            BuddyClimbDiagnostics.LogCarry($"RPCA_StartCarry prefix applying idempotent same-link state: {BuddyClimbDiagnostics.DescribeViews(carrierCharacter, carriedCharacter)}");
            ApplyBuddyClimbCarryState(carrierCharacter, carriedCharacter);
            return false;
        }

        if (existingCarriedPlayer != null)
        {
            BuddyClimbDiagnostics.LogCarry($"RPCA_StartCarry prefix found existing carriedPlayer={BuddyClimbDiagnostics.Describe(existingCarriedPlayer)} before applying new state.");
            if (!TryClearExistingBuddyClimbCarry(carrierCharacter, existingCarriedPlayer, carriedCharacter))
            {
                BuddyClimbDiagnostics.LogCarry($"RPCA_StartCarry prefix returning false because existing carried player is not BuddyClimb-owned stale state: {BuddyClimbDiagnostics.DescribeViews(carrierCharacter, carriedCharacter)}");
                return false;
            }
        }

        BuddyClimbDiagnostics.LogCarry($"RPCA_StartCarry prefix applying BuddyClimb carry state: {BuddyClimbDiagnostics.DescribeViews(carrierCharacter, carriedCharacter)}");
        ApplyBuddyClimbCarryState(carrierCharacter, carriedCharacter);

        return false;
    }

    private static bool TryClearExistingBuddyClimbCarry(
        Character carrierCharacter,
        Character existingCarriedPlayer,
        Character nextCarriedCharacter)
    {
        if (!IsBuddyClimbCarried(existingCarriedPlayer)
            || existingCarriedPlayer.data.carrier != carrierCharacter)
        {
            Plugin.Log.LogDebug(
                $"Ignoring BuddyClimb start carry for {nextCarriedCharacter.characterName} because {carrierCharacter.characterName} is already carrying {existingCarriedPlayer.characterName}.");
            return false;
        }

        Plugin.Log.LogDebug(
            $"Clearing stale BuddyClimb carry state for {existingCarriedPlayer.characterName} before carrying {nextCarriedCharacter.characterName}.");

        existingCarriedPlayer.refs.carriying.ToggleCarryPhysics(false);
        existingCarriedPlayer.data.isCarried = false;
        existingCarriedPlayer.data.carrier = null;
        carrierCharacter.data.carriedPlayer = null;
        BuddyClimbCarriedViewIds.Remove(existingCarriedPlayer.photonView.ViewID);
        CarryInteractionProxy.Disable(existingCarriedPlayer);
        CarriedBackpackVisuals.HideIfForcedVisible(existingCarriedPlayer);

        foreach (Character playerCharacter in PlayerHandler.GetAllPlayerCharacters())
        {
            playerCharacter.refs.afflictions.UpdateWeight();
        }

        return true;
    }

    private static void ApplyBuddyClimbCarryState(Character carrierCharacter, Character carriedCharacter)
    {
        carriedCharacter.refs.carriying.ToggleCarryPhysics(true);
        carriedCharacter.data.isCarried = true;
        carrierCharacter.data.carriedPlayer = carriedCharacter;
        carriedCharacter.data.carrier = carrierCharacter;
        BuddyClimbCarriedViewIds.Add(carriedCharacter.photonView.ViewID);

        foreach (Character playerCharacter in PlayerHandler.GetAllPlayerCharacters())
        {
            playerCharacter.refs.afflictions.UpdateWeight();
        }

        BuddyClimbDiagnostics.LogCarry($"Applied BuddyClimb carry state: {BuddyClimbDiagnostics.DescribeViews(carrierCharacter, carriedCharacter)}");
    }

    [HarmonyPatch(nameof(CharacterCarrying.RPCA_StartCarry))]
    [HarmonyPostfix]
    private static void RPCA_StartCarryPostfix(PhotonView targetView)
    {
        if (targetView != null
            && targetView.GetComponent<Character>() is Character carriedCharacter
            && carriedCharacter.data.isCarried)
        {
            BuddyClimbDiagnostics.LogCarry($"RPCA_StartCarry postfix sees carried state true: {BuddyClimbDiagnostics.Describe(carriedCharacter)}");
            CarryInteractionProxy.Enable(carriedCharacter);
            if (carriedCharacter.IsLocal)
            {
                CarriedBackpackVisuals.Update(carriedCharacter.GetComponent<CharacterBackpackHandler>());
            }
        }
    }
}
