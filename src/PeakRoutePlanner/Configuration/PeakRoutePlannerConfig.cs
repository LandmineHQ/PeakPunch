using System;
using System.IO;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace PeakRoutePlanner.Configuration;

internal static class PeakRoutePlannerConfig
{
    private const int HotReloadDebounceMilliseconds = 250;

    private static readonly object HotReloadLock = new();

    private static FileSystemWatcher? configWatcher;
    private static ConfigFile? configFile;
    private static Timer? reloadTimer;

    internal static ConfigEntry<bool> EnableRoutePlanner { get; private set; } = null!;

    internal static ConfigEntry<KeyboardShortcut> PlanRouteShortcut { get; private set; } = null!;

    internal static ConfigEntry<KeyboardShortcut> ClearRouteShortcut { get; private set; } = null!;

    internal static ConfigEntry<KeyboardShortcut> DebugSampleBlockShortcut { get; private set; } = null!;

    internal static ConfigEntry<bool> RenderDebugAirCells { get; private set; } = null!;

    internal static void Bind(ConfigFile config)
    {
        EnableRoutePlanner = config.Bind("Route Planner", "EnableRoutePlanner", true, "Enable surface sampling and debug rendering.");
        PlanRouteShortcut = config.Bind("Route Planner", "PlanRouteShortcut", new KeyboardShortcut(KeyCode.Comma, KeyCode.LeftAlt), "Shortcut used to invoke the route planner placeholder. Path planning is currently TODO; this shortcut only logs the local player and campfire target positions.");
        ClearRouteShortcut = config.Bind("Route Planner", "ClearRouteShortcut", new KeyboardShortcut(KeyCode.Period, KeyCode.LeftAlt), "Shortcut used to clear current sampling markers and cancel in-progress sampling.");
        DebugSampleBlockShortcut = config.Bind("Route Planner", "DebugSampleBlockShortcut", new KeyboardShortcut(KeyCode.Slash, KeyCode.LeftAlt), "Shortcut used to sample one surface block around the local player and render standable/climbable debug markers. Press again to clear existing markers.");
        RenderDebugAirCells = config.Bind("Route Planner", "RenderDebugAirCells", false, "Render reachable air-boundary debug cubes when using DebugSampleBlockShortcut. Disabled by default because surface markers are usually enough after air-field validation.");
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
            Plugin.Log.LogInfo("Reloaded PeakRoutePlanner config from disk.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to reload PeakRoutePlanner config: {ex.Message}");
        }
    }
}
