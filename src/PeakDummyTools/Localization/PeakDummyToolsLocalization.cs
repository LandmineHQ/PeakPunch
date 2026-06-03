using System.Collections.Generic;

namespace PeakDummyTools.Localization;

internal enum PeakDummyToolsTextKey
{
    DummyPlayerNameFormat,
    SwitchControlToDummy,
    SwitchControlToOriginal,
    DeleteDummy,
}

internal static class PeakDummyToolsLocalization
{
    private enum SupportedLanguage
    {
        English,
        Chinese,
    }

    private static readonly IReadOnlyDictionary<PeakDummyToolsTextKey, string> EnglishText =
        new Dictionary<PeakDummyToolsTextKey, string>
        {
            [PeakDummyToolsTextKey.DummyPlayerNameFormat] = "dummy{0}",
            [PeakDummyToolsTextKey.SwitchControlToDummy] = "Switch control",
            [PeakDummyToolsTextKey.SwitchControlToOriginal] = "Return control",
            [PeakDummyToolsTextKey.DeleteDummy] = "Delete dummy",
        };

    private static readonly IReadOnlyDictionary<PeakDummyToolsTextKey, string> ChineseText =
        new Dictionary<PeakDummyToolsTextKey, string>
        {
            [PeakDummyToolsTextKey.DummyPlayerNameFormat] = "假人{0}",
            [PeakDummyToolsTextKey.SwitchControlToDummy] = "切换操控",
            [PeakDummyToolsTextKey.SwitchControlToOriginal] = "切回本体",
            [PeakDummyToolsTextKey.DeleteDummy] = "删除假人",
        };

    internal static string Format(PeakDummyToolsTextKey key, params object[] args)
    {
        return string.Format(Get(key), args);
    }

    internal static string Get(PeakDummyToolsTextKey key)
    {
        IReadOnlyDictionary<PeakDummyToolsTextKey, string> currentText = GetCurrentLanguage() switch
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
