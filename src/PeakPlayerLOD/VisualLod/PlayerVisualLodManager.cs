using System.Collections.Generic;
using System.Linq;
using PeakPlayerLOD.Configuration;
using UnityEngine;

namespace PeakPlayerLOD.VisualLod;

internal static class PlayerVisualLodManager
{
    private const float MinimumRefreshInterval = 0.1f;

    private static readonly Dictionary<int, PlayerVisualState> PlayerStates = [];
    private static readonly List<CharacterDistance> RemotePlayers = [];
    private static readonly HashSet<int> ActivePlayerIds = [];
    private static readonly HashSet<int> FullDetailPlayerIds = [];
    private static readonly List<int> StalePlayerIds = [];

    private static float nextRefreshTime;

    internal static void Update()
    {
        if (Time.unscaledTime < nextRefreshTime)
        {
            return;
        }

        float refreshInterval = Mathf.Max(MinimumRefreshInterval, PeakPlayerLodConfig.PlayerVisualLodRefreshInterval.Value);
        nextRefreshTime = Time.unscaledTime + refreshInterval;

        if (!PeakPlayerLodConfig.EnablePlayerVisualLod.Value)
        {
            RestoreAll();
            return;
        }

        Character localCharacter = Character.localCharacter;
        if (localCharacter == null)
        {
            RestoreAll();
            return;
        }

        RefreshPlayerStates(localCharacter);
    }

    internal static void Cleanup()
    {
        RestoreAll(clearStates: true);
    }

    private static void RefreshPlayerStates(Character localCharacter)
    {
        ActivePlayerIds.Clear();
        FullDetailPlayerIds.Clear();
        RemotePlayers.Clear();

        int localId = localCharacter.GetInstanceID();
        ActivePlayerIds.Add(localId);
        FullDetailPlayerIds.Add(localId);
        EnsureState(localCharacter).ApplyFullDetailImmediate();

        Vector3 localPosition = GetCharacterPosition(localCharacter);
        foreach (Character character in Character.AllCharacters)
        {
            if (character == null || character.isBot || character == localCharacter)
            {
                continue;
            }

            int playerId = character.GetInstanceID();
            ActivePlayerIds.Add(playerId);

            float distanceSquared = (GetCharacterPosition(character) - localPosition).sqrMagnitude;
            RemotePlayers.Add(new CharacterDistance(character, playerId, distanceSquared));
        }

        RemotePlayers.Sort(static (left, right) => left.DistanceSquared.CompareTo(right.DistanceSquared));

        int fullDetailCount = Mathf.Max(0, PeakPlayerLodConfig.MaxFullDetailRemotePlayers.Value);
        for (int index = 0; index < RemotePlayers.Count && index < fullDetailCount; index++)
        {
            FullDetailPlayerIds.Add(RemotePlayers[index].PlayerId);
        }

        foreach (CharacterDistance remotePlayer in RemotePlayers)
        {
            PlayerVisualState state = EnsureState(remotePlayer.Character);
            if (FullDetailPlayerIds.Contains(remotePlayer.PlayerId))
            {
                state.RequestFullDetail();
            }
            else
            {
                state.RequestLowDetail();
            }
        }

        RemoveStaleStates();
    }

    private static PlayerVisualState EnsureState(Character character)
    {
        int playerId = character.GetInstanceID();
        if (!PlayerStates.TryGetValue(playerId, out PlayerVisualState state))
        {
            state = new PlayerVisualState(character);
            PlayerStates[playerId] = state;
        }

        return state;
    }

    private static void RemoveStaleStates()
    {
        StalePlayerIds.Clear();
        foreach (KeyValuePair<int, PlayerVisualState> pair in PlayerStates)
        {
            if (!ActivePlayerIds.Contains(pair.Key) || pair.Value.IsInvalid)
            {
                StalePlayerIds.Add(pair.Key);
            }
        }

        foreach (int playerId in StalePlayerIds)
        {
            if (PlayerStates.TryGetValue(playerId, out PlayerVisualState state))
            {
                state.Restore();
                PlayerStates.Remove(playerId);
            }
        }
    }

    private static void RestoreAll(bool clearStates = false)
    {
        foreach (PlayerVisualState state in PlayerStates.Values)
        {
            state.Restore();
        }

        if (clearStates)
        {
            PlayerStates.Clear();
        }
    }

    private static Vector3 GetCharacterPosition(Character character)
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

    private readonly struct CharacterDistance
    {
        internal CharacterDistance(Character character, int playerId, float distanceSquared)
        {
            Character = character;
            PlayerId = playerId;
            DistanceSquared = distanceSquared;
        }

        internal Character Character { get; }

        internal int PlayerId { get; }

        internal float DistanceSquared { get; }
    }

    private sealed class PlayerVisualState
    {
        private readonly Character character;
        private readonly Dictionary<Renderer, bool> originalRendererStates = [];
        private readonly List<Renderer> staleRenderers = [];

        private bool isLowDetail;
        private bool hasAppliedLodState;
        private bool hasPendingLodState;
        private bool pendingLowDetail;
        private float pendingSwitchTime;

        internal PlayerVisualState(Character character)
        {
            this.character = character;
        }

        internal bool IsInvalid => character == null;

        internal void ApplyFullDetailImmediate()
        {
            ClearPendingLodSwitch();
            ApplyLod(lowDetail: false);
        }

        internal void RequestFullDetail()
        {
            RequestLod(lowDetail: false);
        }

        internal void RequestLowDetail()
        {
            RequestLod(lowDetail: true);
        }

        internal void Restore()
        {
            ClearPendingLodSwitch();
            ApplyLod(lowDetail: false);
        }

        private void RequestLod(bool lowDetail)
        {
            if (!hasAppliedLodState)
            {
                ApplyLod(lowDetail);
                return;
            }

            if (isLowDetail == lowDetail)
            {
                ClearPendingLodSwitch();
                return;
            }

            float debounceSeconds = Mathf.Max(0f, PeakPlayerLodConfig.PlayerVisualLodSwitchDebounceSeconds.Value);
            if (debounceSeconds <= 0f)
            {
                ApplyLod(lowDetail);
                return;
            }

            if (!hasPendingLodState || pendingLowDetail != lowDetail)
            {
                hasPendingLodState = true;
                pendingLowDetail = lowDetail;
                pendingSwitchTime = Time.unscaledTime + debounceSeconds;
                return;
            }

            if (Time.unscaledTime >= pendingSwitchTime)
            {
                ClearPendingLodSwitch();
                ApplyLod(lowDetail);
            }
        }

        private void ApplyLod(bool lowDetail)
        {
            RefreshOriginalRenderers();

            staleRenderers.Clear();
            foreach (KeyValuePair<Renderer, bool> rendererState in originalRendererStates.ToArray())
            {
                Renderer renderer = rendererState.Key;
                if (renderer == null)
                {
                    staleRenderers.Add(renderer!);
                    continue;
                }

                renderer.enabled = rendererState.Value && (!lowDetail || IsLowDetailRenderer(renderer));
            }

            RemoveStaleRenderers();
            LogTransitionIfNeeded(lowDetail);
            isLowDetail = lowDetail;
            hasAppliedLodState = true;
        }

        private void ClearPendingLodSwitch()
        {
            hasPendingLodState = false;
            pendingLowDetail = false;
            pendingSwitchTime = 0f;
        }

        private void LogTransitionIfNeeded(bool lowDetail)
        {
            if (hasAppliedLodState && isLowDetail == lowDetail)
            {
                return;
            }

            if (!PeakPlayerLodConfig.LogPlayerVisualLodChanges.Value)
            {
                return;
            }

            string targetState = lowDetail ? "low detail" : "full detail";
            string detailText = lowDetail ? $"; skinProxyRenderers={CountLowDetailRenderers()}" : string.Empty;
            Plugin.Log.LogInfo(
                $"Player visual LOD set {character.characterName} to {targetState}; renderers={originalRendererStates.Count}{detailText}.");
        }

        private void RefreshOriginalRenderers()
        {
            if (character == null)
            {
                return;
            }

            AddRenderer(character.refs.mainRenderer);
            AddCustomizationRenderers();

            foreach (Renderer renderer in character.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                AddRenderer(renderer);
            }
        }

        private void AddCustomizationRenderers()
        {
            CharacterCustomization customization = character.refs.customization;
            if (customization == null || customization.refs == null)
            {
                return;
            }

            AddRenderer(customization.refs.mainRenderer);
            AddRenderer(customization.refs.mainRendererShadow);
            AddRenderer(customization.refs.mouthRenderer);
            AddRenderer(customization.refs.accessoryRenderer);
            AddRenderer(customization.refs.shorts);
            AddRenderer(customization.refs.skirt);
            AddRenderer(customization.refs.skirtShadow);
            AddRenderer(customization.refs.shortsShadow);
            AddRenderer(customization.refs.sashRenderer);
            AddRenderer(customization.refs.blindRenderer);
            AddRenderer(customization.refs.chickenRenderer);
            AddRenderer(customization.refs.headShadow);
            AddRenderer(customization.refs.skeletonRenderer);

            AddRenderers(customization.refs.PlayerRenderers);
            AddRenderers(customization.refs.EyeRenderers);
            AddRenderers(customization.refs.playerHats);
            AddRenderers(customization.refs.AllRenderers);
        }

        private void AddRenderers(Renderer[]? renderers)
        {
            if (renderers == null)
            {
                return;
            }

            foreach (Renderer renderer in renderers)
            {
                AddRenderer(renderer);
            }
        }

        private void AddRenderer(Renderer? renderer)
        {
            if (renderer == null || originalRendererStates.ContainsKey(renderer))
            {
                return;
            }

            originalRendererStates[renderer] = renderer.enabled;
        }

        private void RemoveStaleRenderers()
        {
            foreach (Renderer renderer in staleRenderers)
            {
                originalRendererStates.Remove(renderer);
            }

            staleRenderers.Clear();
        }

        private int CountLowDetailRenderers()
        {
            int count = 0;
            foreach (KeyValuePair<Renderer, bool> rendererState in originalRendererStates)
            {
                Renderer renderer = rendererState.Key;
                if (rendererState.Value && renderer != null && IsLowDetailRenderer(renderer))
                {
                    count++;
                }
            }

            return count;
        }

        private bool IsLowDetailRenderer(Renderer renderer)
        {
            CharacterCustomization customization = character.refs.customization;
            if (customization == null || customization.refs == null)
            {
                return renderer == character.refs.mainRenderer;
            }

            if (IsCosmeticRenderer(renderer, customization))
            {
                return false;
            }

            return renderer == character.refs.mainRenderer
                || renderer == customization.refs.mainRenderer
                || renderer == customization.refs.mouthRenderer
                || ContainsRenderer(customization.refs.PlayerRenderers, renderer)
                || ContainsRenderer(customization.refs.EyeRenderers, renderer);
        }

        private static bool IsCosmeticRenderer(Renderer renderer, CharacterCustomization customization)
        {
            return renderer == customization.refs.mainRendererShadow
                || renderer == customization.refs.accessoryRenderer
                || renderer == customization.refs.shorts
                || renderer == customization.refs.skirt
                || renderer == customization.refs.skirtShadow
                || renderer == customization.refs.shortsShadow
                || renderer == customization.refs.sashRenderer
                || renderer == customization.refs.blindRenderer
                || renderer == customization.refs.chickenRenderer
                || renderer == customization.refs.headShadow
                || renderer == customization.refs.skeletonRenderer
                || ContainsRenderer(customization.refs.playerHats, renderer);
        }

        private static bool ContainsRenderer(Renderer[]? renderers, Renderer renderer)
        {
            if (renderers == null)
            {
                return false;
            }

            foreach (Renderer candidate in renderers)
            {
                if (candidate == renderer)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
