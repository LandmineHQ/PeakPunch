using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using PeakRoutePlanner.Configuration;
using PeakRoutePlanner.Planning;
using PeakRoutePlanner.Visualization;

namespace PeakRoutePlanner;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    internal const string PluginId = "com.github.LandmineHQ.PeakRoutePlanner";
    internal const string PluginName = "PeakRoutePlanner";
    internal const string PluginVersion = "0.1.0";

    internal static ManualLogSource Log { get; private set; } = null!;

    private RoutePlannerRuntime? runtime;
    private RoutePathRenderer? pathRenderer;

    private void Awake()
    {
        Log = Logger;

        PeakRoutePlannerConfig.Bind(Config);
        PeakRoutePlannerConfig.EnableHotReload(Config);

        pathRenderer = new RoutePathRenderer();
        runtime = new RoutePlannerRuntime(pathRenderer);

        new Harmony(PluginId).PatchAll(typeof(Plugin).Assembly);

        Log.LogInfo($"Plugin {PluginName} is loaded!");
    }

    private void Update()
    {
        runtime?.Update();
    }

    private void OnDestroy()
    {
        runtime?.Cleanup();
        runtime = null;
        pathRenderer?.Cleanup();
        pathRenderer = null;
        PeakRoutePlannerConfig.DisableHotReload();
    }
}
