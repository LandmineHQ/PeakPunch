using System;
using System.IO;
using System.Threading;
using BepInEx.Configuration;

namespace BuddyClimb.Configuration;

internal static class BuddyClimbConfig
{
    private const int HotReloadDebounceMilliseconds = 250;

    private static readonly object HotReloadLock = new();

    private static FileSystemWatcher? configWatcher;
    private static ConfigFile? configFile;
    private static Timer? reloadTimer;

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
            Plugin.Log.LogInfo("Reloaded BuddyClimb config from disk.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to reload BuddyClimb config: {ex.Message}");
        }
    }
}
