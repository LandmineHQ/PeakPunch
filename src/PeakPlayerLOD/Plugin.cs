using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using PeakPlayerLOD.Configuration;
using PeakPlayerLOD.VisualLod;

namespace PeakPlayerLOD;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    internal const string PluginId = "com.github.LandmineHQ.PeakPlayerLOD";
    internal const string PluginName = "PeakPlayerLOD";
    internal const string PluginVersion = "0.1.0";

    internal static ManualLogSource Log { get; private set; } = null!;

    private void Awake()
    {
        Log = Logger;

        PeakPlayerLodConfig.Bind(Config);
        PeakPlayerLodConfig.EnableHotReload(Config);

        new Harmony(PluginId).PatchAll(typeof(Plugin).Assembly);

        Log.LogInfo($"Plugin {PluginName} is loaded!");
    }

    private void Update()
    {
        PlayerVisualLodManager.Update();
    }

    private void OnDestroy()
    {
        PlayerVisualLodManager.Cleanup();
        PeakPlayerLodConfig.DisableHotReload();
    }
}
