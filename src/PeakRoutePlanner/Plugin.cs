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
    internal const string RoutePlannerBuildMarker = "surface-sampling-tool-20260610";

    internal static ManualLogSource Log { get; private set; } = null!;

    internal static void LogTiming(string message)
    {
#if DEBUG
        Log.LogInfo(message);
#else
        Log.LogDebug(message);
#endif
    }

    private RoutePlannerRuntime? runtime;
    private SamplingWindowRenderer? samplingWindowRenderer;

    private void Awake()
    {
        Log = Logger;

        PeakRoutePlannerConfig.Bind(Config);
        PeakRoutePlannerConfig.EnableHotReload(Config);

        samplingWindowRenderer = new SamplingWindowRenderer();
        runtime = new RoutePlannerRuntime(samplingWindowRenderer);

        new Harmony(PluginId).PatchAll(typeof(Plugin).Assembly);

        Log.LogInfo($"Plugin {PluginName} is loaded! build={RoutePlannerBuildMarker}");
    }

    private void Update()
    {
        runtime?.Update();
    }

    private void OnDestroy()
    {
        runtime?.Cleanup();
        runtime = null;
        samplingWindowRenderer?.Cleanup();
        samplingWindowRenderer = null;
        PeakRoutePlannerConfig.DisableHotReload();
    }
}
