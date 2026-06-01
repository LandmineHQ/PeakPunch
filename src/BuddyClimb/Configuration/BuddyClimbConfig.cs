using System;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace BuddyClimb.Configuration;

internal static class BuddyClimbConfig
{
    private const float HotReloadDebounceSeconds = 0.25f;

    private static readonly object HotReloadLock = new();

    private static FileSystemWatcher? configWatcher;
    private static ConfigFile? configFile;
    private static bool reloadRequested;
    private static bool reloadScheduled;
    private static float reloadAfterTime;

    internal static ConfigEntry<bool> EnableDebugFeatures { get; private set; } = null!;

    internal static ConfigEntry<KeyboardShortcut> SpawnPlayerShortcut { get; private set; } = null!;

    internal static void Bind(ConfigFile config)
    {
        EnableDebugFeatures = config.Bind(
            "Debug",
            "EnableDebugFeatures",
            false,
            "Enable debug-only BuddyClimb features. Keep this disabled during normal gameplay.");

        SpawnPlayerShortcut = config.Bind(
            "Debug",
            "SpawnPlayerShortcut",
            new KeyboardShortcut(KeyCode.G, KeyCode.LeftAlt),
            "When debug features are enabled, host can press this shortcut to spawn a default test player at the local player's position.");
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
            Plugin.Log.LogInfo("Reloaded BuddyClimb config from disk.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to reload BuddyClimb config: {ex.Message}");
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
