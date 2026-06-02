using System;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace PeakPlayerLOD.Configuration;

internal static class PeakPlayerLodConfig
{
    private const float HotReloadDebounceSeconds = 0.25f;

    private static readonly object HotReloadLock = new();

    private static FileSystemWatcher? configWatcher;
    private static ConfigFile? configFile;
    private static bool reloadRequested;
    private static bool reloadScheduled;
    private static float reloadAfterTime;

    internal static ConfigEntry<bool> EnablePlayerVisualLod { get; private set; } = null!;

    internal static ConfigEntry<int> MaxFullDetailRemotePlayers { get; private set; } = null!;

    internal static ConfigEntry<float> PlayerVisualLodRefreshInterval { get; private set; } = null!;

    internal static ConfigEntry<float> PlayerVisualLodSwitchDebounceSeconds { get; private set; } = null!;

    internal static ConfigEntry<bool> LogPlayerVisualLodChanges { get; private set; } = null!;

    internal static void Bind(ConfigFile config)
    {
        EnablePlayerVisualLod = config.Bind(
            "Player Visual LOD",
            "EnablePlayerVisualLod",
            true,
            "Enable player visual LOD for all non-local player characters.");

        MaxFullDetailRemotePlayers = config.Bind(
            "Player Visual LOD",
            "MaxFullDetailRemotePlayers",
            3,
            "Keep this many nearest non-local player characters using their original renderers. The local player is always kept full detail.");

        PlayerVisualLodRefreshInterval = config.Bind(
            "Player Visual LOD",
            "PlayerVisualLodRefreshInterval",
            0.5f,
            "Seconds between player visual LOD refreshes. Lower values react faster but cost more CPU.");

        PlayerVisualLodSwitchDebounceSeconds = config.Bind(
            "Player Visual LOD",
            "PlayerVisualLodSwitchDebounceSeconds",
            1f,
            "Seconds a player's requested LOD state must remain stable before switching renderers. This prevents rapid toggling near the full-detail boundary.");

        LogPlayerVisualLodChanges = config.Bind(
            "Player Visual LOD",
            "LogPlayerVisualLodChanges",
            false,
            "Log player visual LOD transitions and captured renderer counts for troubleshooting.");
    }

    internal static void EnableHotReload(ConfigFile config)
    {
        DisableHotReload();

        configFile = config;
        string? configDirectory = Path.GetDirectoryName(config.ConfigFilePath);
        string configFileName = Path.GetFileName(config.ConfigFilePath);
        if (string.IsNullOrEmpty(configDirectory) || string.IsNullOrEmpty(configFileName) || !Directory.Exists(configDirectory))
        {
            Plugin.Log.LogWarning($"Config hot reload is disabled because the config directory is unavailable: {config.ConfigFilePath}");
            return;
        }

        configWatcher = new FileSystemWatcher(configDirectory, configFileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.CreationTime
                | NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,
        };

        configWatcher.Changed += OnConfigFileChanged;
        configWatcher.Created += OnConfigFileChanged;
        configWatcher.Renamed += OnConfigFileRenamed;
        configWatcher.EnableRaisingEvents = true;
    }

    internal static void ReloadIfChanged()
    {
        ConfigFile? currentConfig = configFile;
        if (currentConfig == null)
        {
            return;
        }

        lock (HotReloadLock)
        {
            if (!reloadRequested)
            {
                return;
            }

            if (!reloadScheduled)
            {
                reloadScheduled = true;
                reloadAfterTime = Time.unscaledTime + HotReloadDebounceSeconds;
                return;
            }

            if (Time.unscaledTime < reloadAfterTime)
            {
                return;
            }

            reloadRequested = false;
            reloadScheduled = false;
        }

        try
        {
            currentConfig.Reload();
            Plugin.Log.LogInfo("Reloaded PeakPlayerLOD config from disk.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to reload PeakPlayerLOD config: {ex.Message}");
        }
    }

    internal static void DisableHotReload()
    {
        if (configWatcher != null)
        {
            configWatcher.EnableRaisingEvents = false;
            configWatcher.Changed -= OnConfigFileChanged;
            configWatcher.Created -= OnConfigFileChanged;
            configWatcher.Renamed -= OnConfigFileRenamed;
            configWatcher.Dispose();
            configWatcher = null;
        }

        lock (HotReloadLock)
        {
            reloadRequested = false;
            reloadScheduled = false;
        }
    }

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs args)
    {
        RequestReload();
    }

    private static void OnConfigFileRenamed(object sender, RenamedEventArgs args)
    {
        RequestReload();
    }

    private static void RequestReload()
    {
        lock (HotReloadLock)
        {
            reloadRequested = true;
            reloadScheduled = false;
        }
    }
}
