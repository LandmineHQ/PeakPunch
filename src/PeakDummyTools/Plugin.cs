using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using PeakDummyTools.Configuration;
using PeakDummyTools.DummyPlayers;

namespace PeakDummyTools;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    internal const string PluginId = "com.github.LandmineHQ.PeakDummyTools";
    internal const string PluginName = "PeakDummyTools";
    internal const string PluginVersion = "0.1.0";

    internal static ManualLogSource Log { get; private set; } = null!;

    private void Awake()
    {
        Log = Logger;

        PeakDummyToolsConfig.Bind(Config);
        PeakDummyToolsConfig.EnableHotReload(Config);

        new Harmony(PluginId).PatchAll(typeof(Plugin).Assembly);

        Log.LogInfo($"Plugin {PluginName} is loaded!");
    }

    private void Update()
    {
        PeakDummyToolsConfig.ReloadIfChanged();
        DummyPlayerSpawner.Update();
    }

    private void OnDestroy()
    {
        PeakDummyToolsConfig.DisableHotReload();
    }
}
