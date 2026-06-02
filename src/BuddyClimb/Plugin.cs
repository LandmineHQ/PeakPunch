using BepInEx;
using BepInEx.Logging;
using BuddyClimb.Compatibility;
using BuddyClimb.Configuration;
using BuddyClimb.Gameplay;
using HarmonyLib;

namespace BuddyClimb;

/// <summary>
/// The BepInEx plugin class of BuddyClimb.
/// </summary>
[BepInAutoPlugin]
[BepInDependency(ModCompatibility.PiggybackPluginId, BepInDependency.DependencyFlags.SoftDependency)]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    private void Awake()
    {
        Log = Logger;

        BuddyClimbConfig.Bind(Config);
        BuddyClimbConfig.EnableHotReload(Config);

        new Harmony(Id).PatchAll(typeof(Plugin).Assembly);

        if (ModCompatibility.IsPiggybackLoaded)
        {
            Log.LogInfo("Piggyback detected; BuddyClimb carry spectate patches are disabled.");
        }

        Log.LogInfo($"Plugin {Name} is loaded!");
    }

    private void Update()
    {
        BuddyClimbConfig.ReloadIfChanged();
        CarriedPlayerDropper.Update();
    }

    private void OnDestroy()
    {
        BuddyClimbConfig.DisableHotReload();
    }
}
