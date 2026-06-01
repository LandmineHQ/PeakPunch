using System.Collections.Generic;
using BuddyClimb.Debugging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(CharacterCarrying))]
internal static class CharacterCarryingPatch
{
    private static readonly HashSet<int> TemporarilyPassedOutViewIds = [];

    [HarmonyPatch("FixedUpdate")]
    [HarmonyPrefix]
    private static void FixedUpdatePrefix(CharacterCarrying __instance)
    {
        Character character = __instance.character ?? __instance.GetComponent<Character>();
        if (character == null
            || character.photonView == null
            || !character.data.isCarried
            || character.data.carrier == null
            || !TemporarilyPassedOutViewIds.Contains(character.photonView.ViewID))
        {
            return;
        }

        SetTemporaryPassOut(character);
    }

    [HarmonyPatch(nameof(CharacterCarrying.RPCA_Drop))]
    [HarmonyPrefix]
    private static bool RPCA_DropPrefix(PhotonView targetView)
    {
        if (targetView == null)
        {
            return true;
        }

        bool wasTemporarilyPassedOut = TemporarilyPassedOutViewIds.Remove(targetView.ViewID);
        if (!targetView.IsMine)
        {
            return true;
        }

        Character character = targetView.GetComponent<Character>();
        Character? carrier = character?.data.carrier;
        if (character != null
            && carrier != null
            && (wasTemporarilyPassedOut || !character.data.fullyPassedOut))
        {
            targetView.RPC("RPCA_UnPassOut", carrier.photonView.Owner);
        }

        return true;
    }

    [HarmonyPatch(nameof(CharacterCarrying.RPCA_StartCarry))]
    [HarmonyPrefix]
    private static bool RPCA_StartCarryPrefix(CharacterCarrying __instance, PhotonView targetView)
    {
        Character carrierCharacter = __instance.character ?? __instance.GetComponent<Character>();
        if (!DebugPlayerSpawner.IsDebugSpawnedPlayer(carrierCharacter))
        {
            return true;
        }

        if (targetView == null)
        {
            return false;
        }

        Character carriedCharacter = targetView.GetComponent<Character>();
        if (carriedCharacter == null)
        {
            return false;
        }

        if (carrierCharacter.data.carriedPlayer != null)
        {
            __instance.Drop(carrierCharacter.data.carriedPlayer);
            return false;
        }

        SetTemporaryPassOut(carriedCharacter);
        carriedCharacter.refs.carriying.ToggleCarryPhysics(true);
        carriedCharacter.data.isCarried = true;
        carrierCharacter.data.carriedPlayer = carriedCharacter;
        carriedCharacter.data.carrier = carrierCharacter;

        foreach (Character playerCharacter in PlayerHandler.GetAllPlayerCharacters())
        {
            playerCharacter.refs.afflictions.UpdateWeight();
        }

        return false;
    }

    private static void SetTemporaryPassOut(Character character)
    {
        TemporarilyPassedOutViewIds.Add(character.photonView.ViewID);
        character.data.passedOut = true;
        character.data.fullyPassedOut = true;
        character.data.passOutValue = 1f;
        character.data.lastPassedOut = Time.time;
    }
}
