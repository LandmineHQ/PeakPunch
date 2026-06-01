using System.Collections.Generic;
using System.Linq;
using PeakPlayerLOD.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace PeakPlayerLOD.VisualLod;

internal static class PlayerVisualLodManager
{
    private const float MinimumRefreshInterval = 0.1f;

    private static readonly Dictionary<int, PlayerVisualState> PlayerStates = [];
    private static readonly List<CharacterDistance> RemotePlayers = [];
    private static readonly HashSet<int> ActivePlayerIds = [];
    private static readonly HashSet<int> FullDetailPlayerIds = [];
    private static readonly List<int> StalePlayerIds = [];

    private static Material? proxyMaterial;
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
        RestoreAll(destroyProxies: true);

        if (proxyMaterial != null)
        {
            Object.Destroy(proxyMaterial);
            proxyMaterial = null;
        }
    }

    private static void RefreshPlayerStates(Character localCharacter)
    {
        ActivePlayerIds.Clear();
        FullDetailPlayerIds.Clear();
        RemotePlayers.Clear();

        int localId = localCharacter.GetInstanceID();
        ActivePlayerIds.Add(localId);
        FullDetailPlayerIds.Add(localId);
        EnsureState(localCharacter).ApplyFullDetail();

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

        bool showProxy = PeakPlayerLodConfig.ShowLowDetailPlayerProxy.Value;
        foreach (CharacterDistance remotePlayer in RemotePlayers)
        {
            PlayerVisualState state = EnsureState(remotePlayer.Character);
            if (FullDetailPlayerIds.Contains(remotePlayer.PlayerId))
            {
                state.ApplyFullDetail();
            }
            else
            {
                state.ApplyLowDetail(showProxy);
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
                state.Restore(destroyProxy: true);
                PlayerStates.Remove(playerId);
            }
        }
    }

    private static void RestoreAll(bool destroyProxies = false)
    {
        foreach (PlayerVisualState state in PlayerStates.Values)
        {
            state.Restore(destroyProxies);
        }

        if (destroyProxies)
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

    private static Material GetProxyMaterial()
    {
        if (proxyMaterial != null)
        {
            return proxyMaterial;
        }

        Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
        proxyMaterial = new Material(shader)
        {
            name = "PeakPlayerLOD Proxy",
            color = new Color(0.2f, 0.9f, 1f, 1f),
        };

        return proxyMaterial;
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

        private GameObject? proxy;
        private bool isLowDetail;

        internal PlayerVisualState(Character character)
        {
            this.character = character;
        }

        internal bool IsInvalid => character == null;

        internal void ApplyFullDetail()
        {
            RefreshOriginalRenderers();
            LogTransitionIfNeeded(lowDetail: false);
            staleRenderers.Clear();
            foreach (KeyValuePair<Renderer, bool> rendererState in originalRendererStates.ToArray())
            {
                Renderer renderer = rendererState.Key;
                if (renderer == null)
                {
                    staleRenderers.Add(renderer!);
                    continue;
                }

                renderer.enabled = rendererState.Value;
            }

            RemoveStaleRenderers();
            SetProxyActive(false);
        }

        internal void ApplyLowDetail(bool showProxy)
        {
            RefreshOriginalRenderers();
            LogTransitionIfNeeded(lowDetail: true);
            staleRenderers.Clear();
            foreach (Renderer renderer in originalRendererStates.Keys.ToArray())
            {
                if (renderer == null)
                {
                    staleRenderers.Add(renderer!);
                    continue;
                }

                renderer.enabled = false;
            }

            RemoveStaleRenderers();
            SetProxyActive(showProxy);
        }

        internal void Restore(bool destroyProxy)
        {
            ApplyFullDetail();
            if (destroyProxy && proxy != null)
            {
                Object.Destroy(proxy);
                proxy = null;
            }
        }

        private void LogTransitionIfNeeded(bool lowDetail)
        {
            if (isLowDetail == lowDetail)
            {
                return;
            }

            isLowDetail = lowDetail;
            if (!PeakPlayerLodConfig.LogPlayerVisualLodChanges.Value)
            {
                return;
            }

            string targetState = lowDetail ? "low detail" : "full detail";
            Plugin.Log.LogInfo(
                $"Player visual LOD set {character.characterName} to {targetState}; renderers={originalRendererStates.Count}.");
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
            if (renderer == null || IsProxyRenderer(renderer) || originalRendererStates.ContainsKey(renderer))
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

        private bool IsProxyRenderer(Renderer renderer)
        {
            return proxy != null && renderer.transform.IsChildOf(proxy.transform);
        }

        private void SetProxyActive(bool active)
        {
            if (!active)
            {
                if (proxy != null)
                {
                    proxy.SetActive(false);
                }

                return;
            }

            EnsureProxy();
            if (proxy != null)
            {
                UpdateProxyTransform();
                proxy.SetActive(true);
            }
        }

        private void EnsureProxy()
        {
            if (proxy != null || character == null)
            {
                return;
            }

            proxy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            proxy.name = "PeakPlayerLOD Proxy";
            proxy.layer = character.gameObject.layer;
            proxy.SetActive(false);
            UpdateProxyTransform();

            Collider collider = proxy.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }

            Renderer renderer = proxy.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = GetProxyMaterial();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private void UpdateProxyTransform()
        {
            if (proxy == null || character == null)
            {
                return;
            }

            proxy.transform.SetParent(null, worldPositionStays: true);
            proxy.transform.position = GetCharacterPosition(character) + new Vector3(0f, 0.35f, 0f);
            proxy.transform.rotation = Quaternion.identity;
            proxy.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
        }
    }
}
