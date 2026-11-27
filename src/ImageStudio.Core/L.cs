using System.Globalization;

namespace ImageStudio.Core;

/// <summary>
/// UI localization. Korean source strings are the keys; <see cref="En"/> holds the English text.
/// Language: "system" (Korean when the Windows UI language is Korean, otherwise English), "ko" or "en".
/// Missing translations fall back to the Korean source (and are listed by the unit test).
/// </summary>
public static partial class L
{
    public static bool IsKorean { get; private set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

    /// <summary>Applies the app setting; call once at startup (a change needs a restart).</summary>
    public static void Init(string? setting) => IsKorean = setting switch
    {
        "ko" => true,
        "en" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko",
    };

    /// <summary>Translate a fixed string.</summary>
    public static string T(string ko) => IsKorean || ko is null ? ko! : En.TryGetValue(ko, out var en) ? en : ko;

    /// <summary>Translate a template with {0}-style placeholders, then format it.</summary>
    public static string F(string ko, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(ko), args);

    public static IReadOnlyDictionary<string, string> English => En;
}
