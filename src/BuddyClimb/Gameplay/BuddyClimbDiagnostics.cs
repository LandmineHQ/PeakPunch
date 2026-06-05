namespace BuddyClimb.Gameplay;

internal static class BuddyClimbDiagnostics
{
    internal static void LogCarry(string message)
    {
#if DEBUG
        Plugin.Log.LogInfo($"[BuddyClimb carry] {message}");
#else
        Plugin.Log.LogDebug($"[BuddyClimb carry] {message}");
#endif
    }

    internal static string Describe(Character? character)
    {
        if (character == null)
        {
            return "<null character>";
        }

        Character? carrier = character.data?.carrier;
        Character? carriedPlayer = character.data?.carriedPlayer;
        string currentItem = character.data?.currentItem != null
            ? character.data.currentItem.GetType().Name
            : "none";

        return $"{GetName(character)}"
            + $" charView={GetCharacterViewId(character)}"
            + $" playerView={GetPlayerViewId(character)}"
            + $" isLocal={character.IsLocal}"
            + $" isBot={character.isBot}"
            + $" dead={character.data?.dead}"
            + $" passedOut={character.data?.passedOut}"
            + $" fullyPassedOut={character.data?.fullyPassedOut}"
            + $" isCarried={character.data?.isCarried}"
            + $" carrier={GetName(carrier)}"
            + $" carriedPlayer={GetName(carriedPlayer)}"
            + $" isCarryingCharacter={character.data?.IsCarryingCharacter}"
            + $" hasBackpack={HasBackpack(character)}"
            + $" currentItem={currentItem}";
    }

    internal static string DescribeViews(Character? carrier, Character? carried)
    {
        return $"carrier=[{Describe(carrier)}], carried=[{Describe(carried)}]";
    }

    private static string GetName(Character? character)
    {
        if (character == null)
        {
            return "null";
        }

        return $"{character.characterName ?? character.name}#{GetCharacterViewId(character)}";
    }

    private static int GetCharacterViewId(Character? character)
    {
        return character?.photonView != null ? character.photonView.ViewID : -1;
    }

    private static int GetPlayerViewId(Character? character)
    {
        return character?.player?.photonView != null ? character.player.photonView.ViewID : -1;
    }

    private static bool HasBackpack(Character? character)
    {
        return character?.player?.backpackSlot is { hasBackpack: true };
    }
}
