using System;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace PeakDummyTools.Configuration;

internal static class PeakDummyToolsConfig
{
    private const float HotReloadDebounceSeconds = 0.25f;

    private static readonly object HotReloadLock = new();

    private static FileSystemWatcher? configWatcher;
    private static ConfigFile? configFile;
    private static bool reloadRequested;
    private static bool reloadScheduled;
    private static float reloadAfterTime;

    internal static ConfigEntry<bool> EnableDummyTools { get; private set; } = null!;

    internal static ConfigEntry<KeyboardShortcut> SpawnDummyShortcut { get; private set; } = null!;

    internal static ConfigEntry<KeyboardShortcut> SwitchControlShortcut { get; private set; } = null!;

    internal static void Bind(ConfigFile config)
    {
        EnableDummyTools = config.Bind(
            "Dummy Spawner",
            "EnableDummyTools",
            false,
            "Enable PEAK dummy-player tools. Keep this disabled during normal gameplay.");

        SpawnDummyShortcut = config.Bind(
            "Dummy Spawner",
            "SpawnDummyShortcut",
            new KeyboardShortcut(KeyCode.G, KeyCode.LeftAlt),
            "When dummy tools are enabled, host can press this shortcut to spawn a dummy player at the local player's position.");

        SwitchControlShortcut = config.Bind(
            "Dummy Control",
            "SwitchControlShortcut",
            new KeyboardShortcut(KeyCode.T, KeyCode.LeftAlt),
            "When dummy tools are enabled, host can press this shortcut while targeting a dummy player to control it. Press again with no dummy target to return to the host character.");
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
            Plugin.Log.LogInfo("Reloaded PeakDummyTools config from disk.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to reload PeakDummyTools config: {ex.Message}");
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
