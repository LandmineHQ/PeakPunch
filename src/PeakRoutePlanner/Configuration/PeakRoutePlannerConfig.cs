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

    internal static ConfigEntry<KeyboardShortcut> DebugVerticalAirColumnShortcut { get; private set; } = null!;

    internal static ConfigEntry<bool> RenderSurfaceSampleMarkers { get; private set; } = null!;

    internal static ConfigEntry<bool> RenderAirCells { get; private set; } = null!;

    internal static ConfigEntry<bool> RenderAirBoundaryProbes { get; private set; } = null!;

    internal static ConfigEntry<int> MaxRoutePlannerSteps { get; private set; } = null!;

    internal static ConfigEntry<float> RouteTargetReachedDistance { get; private set; } = null!;

    internal static ConfigEntry<float> RouteRegionMergeDistance { get; private set; } = null!;

    internal static ConfigEntry<int> RouteEdgeValidationPairLimit { get; private set; } = null!;

    internal static ConfigEntry<bool> RenderRoutePreview { get; private set; } = null!;

    internal static void Bind(ConfigFile config)
    {
        EnableRoutePlanner = config.Bind("Route Planner", "EnableRoutePlanner", true, "Enable surface sampling and debug rendering.");
        PlanRouteShortcut = config.Bind("Route Planner", "PlanRouteShortcut", new KeyboardShortcut(KeyCode.Comma, KeyCode.LeftAlt), "Shortcut used to start incremental route planning from the local player toward the highest campfire target. The planner samples reachable standable regions and renders a preview path; it does not control the player.");
        ClearRouteShortcut = config.Bind("Route Planner", "ClearRouteShortcut", new KeyboardShortcut(KeyCode.Period, KeyCode.LeftAlt), "Shortcut used to clear current sampling markers and cancel in-progress sampling.");
        DebugSampleBlockShortcut = config.Bind("Route Planner", "DebugSampleBlockShortcut", new KeyboardShortcut(KeyCode.Slash, KeyCode.LeftAlt), "Shortcut used to sample one surface block around the local player and render standable/climbable debug markers. Press again to clear existing markers.");
        DebugVerticalAirColumnShortcut = config.Bind("Route Planner", "DebugVerticalAirColumnShortcut", new KeyboardShortcut(KeyCode.Slash, KeyCode.LeftControl), "Shortcut used to generate a vertical air-voxel column from the local player, probe downward at the first boundary, and render the air cells plus sampled surface point.");
        RenderSurfaceSampleMarkers = config.Bind("Route Planner Rendering", "RenderSurfaceSampleMarkers", false, "Render standable/climbable sample marker spheres during route planning. Debug shortcuts Alt+/ and Ctrl+/ ignore this value and always show sample markers.");
        RenderAirCells = config.Bind("Route Planner Rendering", "RenderAirCells", false, "Render reachable air cell cubes during route planning. Debug shortcuts Alt+/ and Ctrl+/ ignore this value and always show air cells.");
        RenderAirBoundaryProbes = config.Bind("Route Planner Rendering", "RenderAirBoundaryProbes", false, "Render air-boundary probe lines during route planning. Debug shortcuts Alt+/ and Ctrl+/ ignore this value and always show probe lines.");
        MaxRoutePlannerSteps = config.Bind("Route Planner", "MaxRoutePlannerSteps", 192, "Maximum incremental sampling/planning steps for one Alt+Comma route-planning run. Values below 192 are raised internally so long campfire routes do not stop at dense rubble fields.");
        RouteTargetReachedDistance = config.Bind("Route Planner", "RouteTargetReachedDistance", 4f, "Route planning stops when the current standable region has a point within this world-space distance of the campfire target.");
        RouteRegionMergeDistance = config.Bind("Route Planner", "RouteRegionMergeDistance", 1.25f, "Maximum horizontal spacing used to merge neighboring standable samples into one route-planning region/set.");
        RouteEdgeValidationPairLimit = config.Bind("Route Planner", "RouteEdgeValidationPairLimit", 64, "Maximum representative point pairs tested when validating whether one standable region can reach another.");
        RenderRoutePreview = config.Bind("Route Planner Rendering", "RenderRoutePreview", true, "Render the currently committed route preview line while incremental Alt+Comma route planning runs.");
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
