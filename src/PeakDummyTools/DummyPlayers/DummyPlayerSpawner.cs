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
    private const int InstantiationSkinIndex = 2;
    private const int InstantiationAccessoryIndex = 3;
    private const int InstantiationEyesIndex = 4;
    private const int InstantiationMouthIndex = 5;
    private const int InstantiationOutfitIndex = 6;
    private const int InstantiationHatIndex = 7;
    private const int InstantiationSashIndex = 8;
    private const int RandomCustomizationAttempts = 8;

    private static readonly HashSet<int> DummySpawnedViewIds = [];
    private static readonly Dictionary<int, DummyPlayerRecord> DummyPlayersByCharacterViewId = [];
    private static readonly Dictionary<Player, int> DummyCharacterViewIdsByPlayer = [];
    private static readonly FieldInfo PlayerViewField = AccessTools.Field(typeof(Player), "view");
    private static readonly MethodInfo OnPlayerDataChangeMethod = AccessTools.Method(
        typeof(CharacterCustomization),
        "OnPlayerDataChange");

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

    internal static void PrepareCustomizationStart(CharacterCustomization customization)
    {
        if (!IsDummyCustomization(customization))
        {
            return;
        }

        customization.ignorePlayerCosmetics = true;
    }

    internal static void FinalizeCustomizationStart(CharacterCustomization customization)
    {
        if (!TryGetDummyCustomizationData(customization, out CharacterCustomizationData customizationData))
        {
            return;
        }

        ApplyDummyCustomization(customization, customizationData);
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
        CharacterCustomizationData customizationData = CreateRandomDummyCustomizationData(GetLocalCustomizationData());

        object[] instantiationData =
        [
            DummyPlayerInstantiationMarker,
            dummyNumber,
            customizationData.currentSkin,
            customizationData.currentAccessory,
            customizationData.currentEyes,
            customizationData.currentMouth,
            customizationData.currentOutfit,
            customizationData.currentHat,
            customizationData.currentSash,
        ];
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

    private static CharacterCustomizationData CreateRandomDummyCustomizationData(CharacterCustomizationData? localCustomizationData)
    {
        CharacterCustomizationData? customizationData = null;
        for (int attempt = 0; attempt < RandomCustomizationAttempts; attempt++)
        {
            customizationData = CreateRandomCustomizationData();
            if (localCustomizationData == null || !HasSameCustomization(customizationData, localCustomizationData))
            {
                return customizationData;
            }
        }

        customizationData ??= CreateRandomCustomizationData();
        if (localCustomizationData != null)
        {
            ForceDifferentCustomization(customizationData, localCustomizationData);
        }

        return customizationData;
    }

    private static CharacterCustomizationData CreateRandomCustomizationData()
    {
        CharacterCustomizationData customizationData = new()
        {
            currentSkin = GetRandomUnlockedIndex(Customization.Type.Skin),
            currentAccessory = GetRandomUnlockedIndex(Customization.Type.Accessory),
            currentEyes = GetRandomUnlockedIndex(Customization.Type.Eyes),
            currentMouth = GetRandomUnlockedIndex(Customization.Type.Mouth),
            currentOutfit = GetRandomUnlockedIndex(Customization.Type.Fit),
            currentHat = GetRandomUnlockedIndex(Customization.Type.Hat),
            currentSash = GetRandomUnlockedIndex(Customization.Type.Sash),
        };
        customizationData.CorrectValues();
        return customizationData;
    }

    private static CharacterCustomizationData? GetLocalCustomizationData()
    {
        try
        {
            PersistentPlayerDataService playerDataService = GameHandler.GetService<PersistentPlayerDataService>();
            return playerDataService.GetPlayerData(PhotonNetwork.LocalPlayer).customizationData;
        }
        catch
        {
            return null;
        }
    }

    private static int GetRandomUnlockedIndex(Customization.Type customizationType)
    {
        Customization customization = Customization.Instance;
        if (customization == null)
        {
            return 0;
        }

        return customization.GetRandomUnlockedIndex(customizationType);
    }

    private static void ForceDifferentCustomization(
        CharacterCustomizationData customizationData,
        CharacterCustomizationData localCustomizationData)
    {
        if (TryGetDifferentUnlockedIndex(Customization.Type.Fit, localCustomizationData.currentOutfit, out int outfitIndex))
        {
            customizationData.currentOutfit = outfitIndex;
            return;
        }

        if (TryGetDifferentUnlockedIndex(Customization.Type.Hat, localCustomizationData.currentHat, out int hatIndex))
        {
            customizationData.currentHat = hatIndex;
            return;
        }

        if (TryGetDifferentUnlockedIndex(Customization.Type.Skin, localCustomizationData.currentSkin, out int skinIndex))
        {
            customizationData.currentSkin = skinIndex;
            return;
        }

        if (TryGetDifferentUnlockedIndex(Customization.Type.Accessory, localCustomizationData.currentAccessory, out int accessoryIndex))
        {
            customizationData.currentAccessory = accessoryIndex;
            return;
        }

        if (TryGetDifferentUnlockedIndex(Customization.Type.Eyes, localCustomizationData.currentEyes, out int eyesIndex))
        {
            customizationData.currentEyes = eyesIndex;
            return;
        }

        if (TryGetDifferentUnlockedIndex(Customization.Type.Mouth, localCustomizationData.currentMouth, out int mouthIndex))
        {
            customizationData.currentMouth = mouthIndex;
            return;
        }

        if (TryGetDifferentUnlockedIndex(Customization.Type.Sash, localCustomizationData.currentSash, out int sashIndex))
        {
            customizationData.currentSash = sashIndex;
        }
    }

    private static bool TryGetDifferentUnlockedIndex(
        Customization.Type customizationType,
        int currentIndex,
        out int differentIndex)
    {
        differentIndex = 0;
        Customization customization = Customization.Instance;
        if (customization == null)
        {
            return false;
        }

        CustomizationOption[] options = customization.GetList(customizationType);
        List<int> candidates = [];
        for (int index = 0; index < options.Length; index++)
        {
            if (index != currentIndex && !options[index].IsLocked)
            {
                candidates.Add(index);
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        differentIndex = candidates[Random.Range(0, candidates.Count)];
        return true;
    }

    private static bool HasSameCustomization(CharacterCustomizationData left, CharacterCustomizationData right)
    {
        return left.currentSkin == right.currentSkin
            && left.currentAccessory == right.currentAccessory
            && left.currentEyes == right.currentEyes
            && left.currentMouth == right.currentMouth
            && left.currentOutfit == right.currentOutfit
            && left.currentHat == right.currentHat
            && left.currentSash == right.currentSash;
    }

    private static bool IsDummyCustomization(CharacterCustomization customization)
    {
        if (customization == null)
        {
            return false;
        }

        PhotonView photonView = customization.GetComponent<PhotonView>();
        return TryGetDummyNumber(photonView, out _);
    }

    private static bool TryGetDummyCustomizationData(
        CharacterCustomization customization,
        out CharacterCustomizationData customizationData)
    {
        customizationData = null!;
        if (customization == null)
        {
            return false;
        }

        PhotonView photonView = customization.GetComponent<PhotonView>();
        return TryGetDummyCustomizationData(photonView, out customizationData);
    }

    private static bool TryGetDummyCustomizationData(
        PhotonView? photonView,
        out CharacterCustomizationData customizationData)
    {
        customizationData = null!;
        if (!TryGetDummyNumber(photonView, out _))
        {
            return false;
        }

        object[] instantiationData = photonView!.InstantiationData;
        if (instantiationData == null || instantiationData.Length <= InstantiationSashIndex)
        {
            return false;
        }

        if (instantiationData[InstantiationSkinIndex] is not int skinIndex
            || instantiationData[InstantiationAccessoryIndex] is not int accessoryIndex
            || instantiationData[InstantiationEyesIndex] is not int eyesIndex
            || instantiationData[InstantiationMouthIndex] is not int mouthIndex
            || instantiationData[InstantiationOutfitIndex] is not int outfitIndex
            || instantiationData[InstantiationHatIndex] is not int hatIndex
            || instantiationData[InstantiationSashIndex] is not int sashIndex)
        {
            return false;
        }

        customizationData = new CharacterCustomizationData
        {
            currentSkin = skinIndex,
            currentAccessory = accessoryIndex,
            currentEyes = eyesIndex,
            currentMouth = mouthIndex,
            currentOutfit = outfitIndex,
            currentHat = hatIndex,
            currentSash = sashIndex,
        };
        customizationData.CorrectValues();
        return true;
    }

    private static void ApplyDummyCustomization(
        CharacterCustomization customization,
        CharacterCustomizationData customizationData)
    {
        if (OnPlayerDataChangeMethod == null)
        {
            Plugin.Log.LogWarning("Unable to apply dummy customization because CharacterCustomization.OnPlayerDataChange was not found.");
            return;
        }

        PersistentPlayerData playerData = new()
        {
            customizationData = customizationData,
        };

        try
        {
            customization.ignorePlayerCosmetics = false;
            OnPlayerDataChangeMethod.Invoke(customization, [playerData]);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to apply dummy customization: {ex.Message}");
        }
        finally
        {
            customization.ignorePlayerCosmetics = true;
        }
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
