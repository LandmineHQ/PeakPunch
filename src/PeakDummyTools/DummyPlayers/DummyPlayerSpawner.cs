using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakDummyTools.Configuration;
using PeakDummyTools.Localization;
using Photon.Pun;
using UnityEngine;

namespace PeakDummyTools.DummyPlayers;

internal static class DummyPlayerSpawner
{
    private const string CharacterPrefabName = "Character";
    private const string DummyPlayerInstantiationMarker = "PeakDummyTools.DummyPlayer";
    private const int InstantiationMarkerIndex = 0;
    private const int InstantiationDummyNumberIndex = 1;

    private static readonly HashSet<int> DummySpawnedViewIds = [];
    private static readonly Dictionary<int, DummyPlayerRecord> DummyPlayersByCharacterViewId = [];
    private static readonly Dictionary<Player, int> DummyCharacterViewIdsByPlayer = [];
    private static readonly FieldInfo PlayerViewField = AccessTools.Field(typeof(Player), "view");

    private static int nextDummyNumber = 1;
    private static bool creatingSyntheticPlayer;
    private static string pendingSyntheticPlayerName = string.Empty;

    private sealed class DummyPlayerRecord
    {
        internal DummyPlayerRecord(int dummyNumber, Player player)
        {
            DummyNumber = dummyNumber;
            Player = player;
            UserId = $"{DummyPlayerInstantiationMarker}.{dummyNumber}";
            ActorNumber = -10_000 - dummyNumber;
        }

        internal int DummyNumber { get; }

        internal Player Player { get; }

        internal string UserId { get; }

        internal int ActorNumber { get; }
    }

    internal static void Update()
    {
        if (!PeakDummyToolsConfig.EnableDummyTools.Value)
        {
            return;
        }

        if (!PeakDummyToolsConfig.SpawnDummyShortcut.Value.IsDown())
        {
            return;
        }

        SpawnAtLocalPlayerPosition();
    }

    internal static bool IsDummyPlayer(Character character)
    {
        if (character == null || character.photonView == null)
        {
            return false;
        }

        return DummySpawnedViewIds.Contains(character.photonView.ViewID)
            || TryGetDummyNumber(character.photonView, out _);
    }

    internal static void PrepareCharacterAwake(Character character)
    {
        PhotonView photonView = character.GetComponent<PhotonView>();
        if (photonView == null || !TryGetDummyNumber(photonView, out _))
        {
            return;
        }

        character.isBot = true;
    }

    internal static void FinalizeCharacterAwake(Character character)
    {
        if (character == null || character.photonView == null || !TryGetDummyNumber(character.photonView, out int dummyNumber))
        {
            return;
        }

        DummySpawnedViewIds.Add(character.photonView.ViewID);
        EnsureDummyPlayer(character, dummyNumber);
        ApplyDummyName(character, dummyNumber);
        character.isBot = false;
        Character.AllBotCharacters.Remove(character);
        if (!Character.AllCharacters.Contains(character))
        {
            Character.AllCharacters.Add(character);
        }

        character.data.dead = false;
        character.data.fullyPassedOut = false;
        character.data.carrier = null;
        character.data.carriedPlayer = null;
    }

    internal static void FinalizeCharacterStart(Character character)
    {
        if (TryGetDummyNumber(character.photonView, out int dummyNumber))
        {
            ApplyDummyName(character, dummyNumber);
        }
    }

    internal static void RemoveDummyPlayer(Character character)
    {
        if (character == null || character.photonView == null)
        {
            return;
        }

        int viewId = character.photonView.ViewID;
        DummySpawnedViewIds.Remove(viewId);
        if (!DummyPlayersByCharacterViewId.TryGetValue(viewId, out DummyPlayerRecord record))
        {
            return;
        }

        DummyPlayersByCharacterViewId.Remove(viewId);
        DummyCharacterViewIdsByPlayer.Remove(record.Player);
        if (record.Player != null)
        {
            Object.Destroy(record.Player.gameObject);
        }
    }

    internal static bool TryGetDummyPlayer(Character character, out Player player)
    {
        player = null!;
        if (character == null || character.photonView == null || !TryGetDummyNumber(character.photonView, out int dummyNumber))
        {
            return false;
        }

        player = EnsureDummyPlayer(character, dummyNumber);
        return player != null;
    }

    internal static bool TryGetDummyCharacter(Player player, out Character character)
    {
        character = null!;
        if (player == null || !DummyCharacterViewIdsByPlayer.TryGetValue(player, out int viewId))
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

    internal static bool TryGetDummyPlayerUserId(Player player, out string userId)
    {
        userId = string.Empty;
        if (player == null || !DummyCharacterViewIdsByPlayer.TryGetValue(player, out int viewId))
        {
            return false;
        }

        if (!DummyPlayersByCharacterViewId.TryGetValue(viewId, out DummyPlayerRecord record))
        {
            return false;
        }

        userId = record.UserId;
        return true;
    }

    internal static bool TryGetDummyPlayerActorNumber(Player player, out int actorNumber)
    {
        actorNumber = 0;
        if (player == null || !DummyCharacterViewIdsByPlayer.TryGetValue(player, out int viewId))
        {
            return false;
        }

        if (!DummyPlayersByCharacterViewId.TryGetValue(viewId, out DummyPlayerRecord record))
        {
            return false;
        }

        actorNumber = record.ActorNumber;
        return true;
    }

    internal static bool TryGetDummyPlayerName(Character character, out string name)
    {
        name = string.Empty;
        if (character == null || character.photonView == null || !TryGetDummyNumber(character.photonView, out int dummyNumber))
        {
            return false;
        }

        name = GetDummyPlayerName(dummyNumber);
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
            Plugin.Log.LogWarning("Dummy player spawn is host-only and requires an active Photon room.");
            return;
        }

        Character localCharacter = Character.localCharacter;
        if (localCharacter == null)
        {
            Plugin.Log.LogWarning("Dummy player spawn skipped because no local character exists.");
            return;
        }

        PlayerHandler playerHandler = PlayerHandler.Instance;
        if (playerHandler == null)
        {
            Plugin.Log.LogWarning("Dummy player spawn skipped because PlayerHandler is unavailable.");
            return;
        }

        int localActorNumber = PhotonNetwork.LocalPlayer.ActorNumber;
        int dummyNumber = nextDummyNumber++;
        Vector3 spawnPosition = GetCharacterSpawnPosition(localCharacter);
        Quaternion spawnRotation = localCharacter.transform.rotation;

        object[] instantiationData = [DummyPlayerInstantiationMarker, dummyNumber];
        GameObject spawnedObject = PhotonNetwork.Instantiate(CharacterPrefabName, spawnPosition, spawnRotation, 0, instantiationData);
        Character spawnedCharacter = spawnedObject.GetComponent<Character>();
        if (spawnedCharacter == null)
        {
            Plugin.Log.LogWarning("Dummy player spawn failed because the spawned object has no Character component.");
            return;
        }

        ConfigureDummyPlayer(spawnedCharacter, localCharacter, playerHandler, localActorNumber);
        spawnedCharacter.photonView.RPC("WarpPlayerRPC", RpcTarget.All, spawnPosition, false);
        Plugin.Log.LogInfo($"Spawned dummy player at {spawnPosition}.");
    }

    private static void ConfigureDummyPlayer(
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

    private static Player EnsureDummyPlayer(Character character, int dummyNumber)
    {
        int viewId = character.photonView.ViewID;
        if (DummyPlayersByCharacterViewId.TryGetValue(viewId, out DummyPlayerRecord existingRecord) && existingRecord.Player != null)
        {
            return existingRecord.Player;
        }

        string playerName = GetDummyPlayerName(dummyNumber);
        pendingSyntheticPlayerName = playerName;
        creatingSyntheticPlayer = true;
        Player player;
        try
        {
            GameObject playerObject = new GameObject($"PeakDummyTools Player [{playerName}]");
            playerObject.AddComponent<PhotonView>();
            player = playerObject.AddComponent<Player>();
        }
        finally
        {
            creatingSyntheticPlayer = false;
            pendingSyntheticPlayerName = string.Empty;
        }

        InitializeSyntheticPlayer(player, playerName);
        DummyPlayerRecord record = new(dummyNumber, player);
        DummyPlayersByCharacterViewId[viewId] = record;
        DummyCharacterViewIdsByPlayer[player] = viewId;
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
        player.gameObject.name = $"PeakDummyTools Player [{playerName}]";
    }

    private static void ApplyDummyName(Character character, int dummyNumber)
    {
        character.gameObject.name = GetDummyPlayerName(dummyNumber);
    }

    private static string GetDummyPlayerName(int dummyNumber)
    {
        return PeakDummyToolsLocalization.Format(PeakDummyToolsTextKey.DummyPlayerNameFormat, dummyNumber);
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

    private static bool TryGetDummyNumber(PhotonView? photonView, out int dummyNumber)
    {
        dummyNumber = 0;
        if (photonView == null)
        {
            return false;
        }

        object[] instantiationData = photonView.InstantiationData;
        if (instantiationData == null || instantiationData.Length <= InstantiationDummyNumberIndex)
        {
            return false;
        }

        if (instantiationData[InstantiationMarkerIndex] is not string marker || marker != DummyPlayerInstantiationMarker)
        {
            return false;
        }

        if (instantiationData[InstantiationDummyNumberIndex] is int number)
        {
            dummyNumber = number;
            return true;
        }

        return false;
    }
}
