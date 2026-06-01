using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace BuddyClimb;

/// <summary>
/// The BepInEx plugin class of BuddyClimb.
/// </summary>
[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    private void Awake()
    {
        Log = Logger;

        new Harmony(Id).PatchAll(typeof(Plugin).Assembly);

        Log.LogInfo($"Plugin {Name} is loaded!");
    }
}
