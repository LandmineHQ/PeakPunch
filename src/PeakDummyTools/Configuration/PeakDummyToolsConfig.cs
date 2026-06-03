using System;
using System.IO;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace PeakDummyTools.Configuration;

internal static class PeakDummyToolsConfig
{
    private const int HotReloadDebounceMilliseconds = 250;

    private static readonly object HotReloadLock = new();

    private static FileSystemWatcher? configWatcher;
    private static ConfigFile? configFile;
    private static Timer? reloadTimer;

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
            "When dummy tools are enabled, the local client can press this shortcut to spawn a dummy player at the local player's position.");

        SwitchControlShortcut = config.Bind(
            "Dummy Control",
            "SwitchControlShortcut",
            new KeyboardShortcut(KeyCode.T, KeyCode.LeftAlt),
            "When dummy tools are enabled, the local client can press this shortcut while targeting an owned dummy player to control it. Press again with no dummy target to return to the original local character.");
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

        reloadTimer = new Timer(ReloadConfigFromTimer);
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
        configWatcher.Renamed += OnConfigFileChanged;
        configWatcher.EnableRaisingEvents = true;
    }

    internal static void DisableHotReload()
    {
        if (configWatcher != null)
        {
            configWatcher.EnableRaisingEvents = false;
            configWatcher.Changed -= OnConfigFileChanged;
            configWatcher.Created -= OnConfigFileChanged;
            configWatcher.Renamed -= OnConfigFileChanged;
            configWatcher.Dispose();
            configWatcher = null;
        }

        lock (HotReloadLock)
        {
            reloadTimer?.Dispose();
            reloadTimer = null;
            configFile = null;
        }
    }

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs args)
    {
        lock (HotReloadLock)
        {
            reloadTimer?.Change(HotReloadDebounceMilliseconds, Timeout.Infinite);
        }
    }

    private static void ReloadConfigFromTimer(object? state)
    {
        ConfigFile? currentConfig;
        lock (HotReloadLock)
        {
            currentConfig = configFile;
        }

        if (currentConfig == null)
        {
            return;
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
}
