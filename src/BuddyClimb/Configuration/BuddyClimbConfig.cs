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

    internal static ConfigEntry<bool> EnableBackpackTransfer { get; private set; } = null!;

    internal static void Bind(ConfigFile config)
    {
        EnableBackpackTransfer = config.Bind(
            "Backpack",
            "EnableBackpackTransfer",
            false,
            "Allow climbing onto players who are wearing a backpack. When enabled, the carrier's backpack is moved to the carried player; if the carried player already has a backpack, their old backpack is dropped first.");
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
