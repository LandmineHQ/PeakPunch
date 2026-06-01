using System.Collections.Generic;
using BuddyClimb.Configuration;
using Photon.Pun;
using UnityEngine;

namespace BuddyClimb.Debugging;

internal static class DebugPlayerSpawner
{
    private const string CharacterPrefabName = "Character";

    private static readonly HashSet<int> DebugSpawnedViewIds = [];

    internal static void Update()
    {
        if (!BuddyClimbConfig.EnableDebugFeatures.Value)
        {
            return;
        }

        if (!BuddyClimbConfig.SpawnPlayerShortcut.Value.IsDown())
        {
            return;
        }

        SpawnAtLocalPlayerPosition();
    }

    internal static bool IsDebugSpawnedPlayer(Character character)
    {
        if (character == null || character.photonView == null)
        {
            return false;
        }

        return DebugSpawnedViewIds.Contains(character.photonView.ViewID);
    }

    private static void SpawnAtLocalPlayerPosition()
    {
        if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient)
        {
            Plugin.Log.LogWarning("Debug player spawn is host-only and requires an active Photon room.");
            return;
        }

        Character localCharacter = Character.localCharacter;
        if (localCharacter == null)
        {
            Plugin.Log.LogWarning("Debug player spawn skipped because no local character exists.");
            return;
        }

        PlayerHandler playerHandler = PlayerHandler.Instance;
        if (playerHandler == null)
        {
            Plugin.Log.LogWarning("Debug player spawn skipped because PlayerHandler is unavailable.");
            return;
        }

        int localActorNumber = PhotonNetwork.LocalPlayer.ActorNumber;
        Vector3 spawnPosition = localCharacter.transform.position;
        Quaternion spawnRotation = localCharacter.transform.rotation;

        GameObject spawnedObject = PhotonNetwork.Instantiate(CharacterPrefabName, spawnPosition, spawnRotation, 0);
        Character spawnedCharacter = spawnedObject.GetComponent<Character>();
        if (spawnedCharacter == null)
        {
            Plugin.Log.LogWarning("Debug player spawn failed because the spawned object has no Character component.");
            return;
        }

        ConfigureDebugPlayer(spawnedCharacter, localCharacter, playerHandler, localActorNumber);
        Plugin.Log.LogInfo($"Spawned debug player at {spawnPosition}.");
    }

    private static void ConfigureDebugPlayer(
        Character spawnedCharacter,
        Character localCharacter,
        PlayerHandler playerHandler,
        int localActorNumber)
    {
        DebugSpawnedViewIds.Add(spawnedCharacter.photonView.ViewID);

        spawnedCharacter.isBot = true;
        spawnedCharacter.gameObject.name = "BuddyClimb Debug Player";
        spawnedCharacter.data.dead = false;
        spawnedCharacter.data.fullyPassedOut = false;
        spawnedCharacter.data.carrier = null;
        spawnedCharacter.data.carriedPlayer = null;

        // Photon spawns the debug Character as host-owned, so restore the real local player mapping.
        Character.localCharacter = localCharacter;
        playerHandler.m_playerCharacterLookup[localActorNumber] = localCharacter;
    }
}
