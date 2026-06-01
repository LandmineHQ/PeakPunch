using System.Collections.Generic;

namespace BuddyClimb.Localization;

internal enum BuddyClimbTextKey
{
    ClimbOnTeammate,
}

internal static class BuddyClimbLocalization
{
    private enum SupportedLanguage
    {
        English,
        Chinese,
    }

    private static readonly IReadOnlyDictionary<BuddyClimbTextKey, string> EnglishText =
        new Dictionary<BuddyClimbTextKey, string>
        {
            [BuddyClimbTextKey.ClimbOnTeammate] = "Climb on!",
        };

    private static readonly IReadOnlyDictionary<BuddyClimbTextKey, string> ChineseText =
        new Dictionary<BuddyClimbTextKey, string>
        {
            [BuddyClimbTextKey.ClimbOnTeammate] = "爬上去!",
        };

    internal static string Get(BuddyClimbTextKey key)
    {
        IReadOnlyDictionary<BuddyClimbTextKey, string> currentText = GetCurrentLanguage() switch
        {
            SupportedLanguage.Chinese => ChineseText,
            _ => EnglishText,
        };

        if (currentText.TryGetValue(key, out string value))
        {
            return value;
        }

        return EnglishText[key];
    }

    private static SupportedLanguage GetCurrentLanguage()
    {
        return LocalizedText.CURRENT_LANGUAGE switch
        {
            LocalizedText.Language.SimplifiedChinese => SupportedLanguage.Chinese,
            LocalizedText.Language.TraditionalChinese => SupportedLanguage.Chinese,
            _ => SupportedLanguage.English,
        };
    }
}
