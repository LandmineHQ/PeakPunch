using BepInEx.Bootstrap;

namespace BuddyClimb.Compatibility;

internal static class ModCompatibility
{
    internal const string PiggybackPluginId = "nakazora.peak.piggyback";

    internal static bool IsPiggybackLoaded => Chainloader.PluginInfos.ContainsKey(PiggybackPluginId);
}
