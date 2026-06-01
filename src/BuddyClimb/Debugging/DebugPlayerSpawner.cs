using System.Collections.Generic;
using System.Reflection;
using BuddyClimb.Configuration;
using BuddyClimb.Localization;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace BuddyClimb.Debugging;

internal static class DebugPlayerSpawner
{
    private const string CharacterPrefabName = "Character";
    private const string DebugPlayerInstantiationMarker = "BuddyClimb.DebugPlayer";
    private const int InstantiationMarkerIndex = 0;
    private const int InstantiationBotNumberIndex = 1;

    private static readonly HashSet<int> DebugSpawnedViewIds = [];
    private static readonly Dictionary<int, DebugPlayerRecord> DebugPlayersByCharacterViewId = [];
    private static readonly Dictionary<Player, int> DebugCharacterViewIdsByPlayer = [];
    private static readonly FieldInfo PlayerViewField = AccessTools.Field(typeof(Player), "view");

    private static int nextBotNumber = 1;
    private static bool creatingSyntheticPlayer;
    private static string pendingSyntheticPlayerName = string.Empty;

    private sealed class DebugPlayerRecord
    {
        internal DebugPlayerRecord(int botNumber, Player player)
        {
            BotNumber = botNumber;
            Player = player;
            UserId = $"{DebugPlayerInstantiationMarker}.{botNumber}";
            ActorNumber = -10_000 - botNumber;
        }

        internal int BotNumber { get; }

        internal Player Player { get; }

        internal string UserId { get; }

        internal int ActorNumber { get; }
    }

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

        return DebugSpawnedViewIds.Contains(character.photonView.ViewID)
            || TryGetDebugBotNumber(character.photonView, out _);
    }

    internal static void PrepareCharacterAwake(Character character)
    {
        PhotonView photonView = character.GetComponent<PhotonView>();
        if (photonView == null || !TryGetDebugBotNumber(photonView, out _))
        {
            return;
        }

        character.isBot = true;
    }

    internal static void FinalizeCharacterAwake(Character character)
    {
        if (character == null || character.photonView == null || !TryGetDebugBotNumber(character.photonView, out int botNumber))
        {
            return;
        }

        DebugSpawnedViewIds.Add(character.photonView.ViewID);
        EnsureDebugPlayer(character, botNumber);
        ApplyDebugName(character, botNumber);
        character.data.dead = false;
        character.data.fullyPassedOut = false;
        character.data.carrier = null;
        character.data.carriedPlayer = null;
    }

    internal static void FinalizeCharacterStart(Character character)
    {
        if (TryGetDebugBotNumber(character.photonView, out int botNumber))
        {
            ApplyDebugName(character, botNumber);
        }
    }

    internal static void RemoveDebugPlayer(Character character)
    {
        if (character == null || character.photonView == null)
        {
            return;
        }

        int viewId = character.photonView.ViewID;
        DebugSpawnedViewIds.Remove(viewId);
        if (!DebugPlayersByCharacterViewId.TryGetValue(viewId, out DebugPlayerRecord record))
        {
            return;
        }

        DebugPlayersByCharacterViewId.Remove(viewId);
        DebugCharacterViewIdsByPlayer.Remove(record.Player);
        if (record.Player != null)
        {
            Object.Destroy(record.Player.gameObject);
        }
    }

    internal static bool TryGetDebugPlayer(Character character, out Player player)
    {
        player = null!;
        if (character == null || character.photonView == null || !TryGetDebugBotNumber(character.photonView, out int botNumber))
        {
            return false;
        }

        player = EnsureDebugPlayer(character, botNumber);
        return player != null;
    }

    internal static bool TryGetDebugCharacter(Player player, out Character character)
    {
        character = null!;
        if (player == null || !DebugCharacterViewIdsByPlayer.TryGetValue(player, out int viewId))
        {
            return false;
        }

        PhotonView characterView = PhotonView.Find(viewId);
        if (characterView == null)
        {
            return false;
        }

        character = characterView.GetComponent<Character>();
        return character != null;
    }

    internal static bool TryGetDebugPlayerUserId(Player player, out string userId)
    {
        userId = string.Empty;
        if (player == null || !DebugCharacterViewIdsByPlayer.TryGetValue(player, out int viewId))
        {
            return false;
        }

        if (!DebugPlayersByCharacterViewId.TryGetValue(viewId, out DebugPlayerRecord record))
        {
            return false;
        }

        userId = record.UserId;
        return true;
    }

    internal static bool TryGetDebugPlayerActorNumber(Player player, out int actorNumber)
    {
        actorNumber = 0;
        if (player == null || !DebugCharacterViewIdsByPlayer.TryGetValue(player, out int viewId))
        {
            return false;
        }

        if (!DebugPlayersByCharacterViewId.TryGetValue(viewId, out DebugPlayerRecord record))
        {
            return false;
        }

        actorNumber = record.ActorNumber;
        return true;
    }

    internal static bool TryGetDebugPlayerName(Character character, out string name)
    {
        name = string.Empty;
        if (character == null || character.photonView == null || !TryGetDebugBotNumber(character.photonView, out int botNumber))
        {
            return false;
        }

        name = GetDebugPlayerName(botNumber);
        return true;
    }

    internal static bool TryInitializeSyntheticPlayerAwake(Player player)
    {
        if (!creatingSyntheticPlayer)
        {
            return false;
        }

        InitializeSyntheticPlayer(player, pendingSyntheticPlayerName);
        return true;
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
        int botNumber = nextBotNumber++;
        Vector3 spawnPosition = GetCharacterSpawnPosition(localCharacter);
        Quaternion spawnRotation = localCharacter.transform.rotation;

        object[] instantiationData = [DebugPlayerInstantiationMarker, botNumber];
        GameObject spawnedObject = PhotonNetwork.Instantiate(CharacterPrefabName, spawnPosition, spawnRotation, 0, instantiationData);
        Character spawnedCharacter = spawnedObject.GetComponent<Character>();
        if (spawnedCharacter == null)
        {
            Plugin.Log.LogWarning("Debug player spawn failed because the spawned object has no Character component.");
            return;
        }

        ConfigureDebugPlayer(spawnedCharacter, localCharacter, playerHandler, localActorNumber);
        spawnedCharacter.photonView.RPC("WarpPlayerRPC", RpcTarget.All, spawnPosition, false);
        Plugin.Log.LogInfo($"Spawned debug player at {spawnPosition}.");
    }

    private static void ConfigureDebugPlayer(
        Character spawnedCharacter,
        Character localCharacter,
        PlayerHandler playerHandler,
        int localActorNumber)
    {
        FinalizeCharacterAwake(spawnedCharacter);

        // Keep this fallback even though the Awake prefix prevents PEAK from replacing the host's character.
        Character.localCharacter = localCharacter;
        playerHandler.m_playerCharacterLookup[localActorNumber] = localCharacter;
        if (!localCharacter.gameObject.activeSelf)
        {
            localCharacter.gameObject.SetActive(true);
        }
    }

    private static Player EnsureDebugPlayer(Character character, int botNumber)
    {
        int viewId = character.photonView.ViewID;
        if (DebugPlayersByCharacterViewId.TryGetValue(viewId, out DebugPlayerRecord existingRecord) && existingRecord.Player != null)
        {
            return existingRecord.Player;
        }

        string playerName = GetDebugPlayerName(botNumber);
        pendingSyntheticPlayerName = playerName;
        creatingSyntheticPlayer = true;
        Player player;
        try
        {
            GameObject playerObject = new GameObject($"BuddyClimb Debug Player [{playerName}]");
            playerObject.AddComponent<PhotonView>();
            player = playerObject.AddComponent<Player>();
        }
        finally
        {
            creatingSyntheticPlayer = false;
            pendingSyntheticPlayerName = string.Empty;
        }

        InitializeSyntheticPlayer(player, playerName);
        DebugPlayerRecord record = new(botNumber, player);
        DebugPlayersByCharacterViewId[viewId] = record;
        DebugCharacterViewIdsByPlayer[player] = viewId;
        return player;
    }

    private static void InitializeSyntheticPlayer(Player player, string playerName)
    {
        PhotonView playerView = player.GetComponent<PhotonView>();
        PlayerViewField?.SetValue(player, playerView);
        player.itemSlots = new ItemSlot[3];
        for (byte slot = 0; slot < player.itemSlots.Length; slot++)
        {
            player.itemSlots[slot] = new ItemSlot(slot);
        }

        player.tempFullSlot = new TemporaryItemSlot(250);
        player.backpackSlot = new BackpackSlot(3);
        player.gameObject.name = $"BuddyClimb Debug Player [{playerName}]";
    }

    private static void ApplyDebugName(Character character, int botNumber)
    {
        character.gameObject.name = GetDebugPlayerName(botNumber);
    }

    private static string GetDebugPlayerName(int botNumber)
    {
        return BuddyClimbLocalization.Format(BuddyClimbTextKey.DebugBotNameFormat, botNumber);
    }

    private static Vector3 GetCharacterSpawnPosition(Character character)
    {
        try
        {
            return character.Center;
        }
        catch
        {
            return character.transform.position;
        }
    }

    private static bool TryGetDebugBotNumber(PhotonView? photonView, out int botNumber)
    {
        botNumber = 0;
        if (photonView == null)
        {
            return false;
        }

        object[] instantiationData = photonView.InstantiationData;
        if (instantiationData == null || instantiationData.Length <= InstantiationBotNumberIndex)
        {
            return false;
        }

        if (instantiationData[InstantiationMarkerIndex] is not string marker || marker != DebugPlayerInstantiationMarker)
        {
            return false;
        }

        if (instantiationData[InstantiationBotNumberIndex] is int number)
        {
            botNumber = number;
            return true;
        }

        return false;
    }
}
