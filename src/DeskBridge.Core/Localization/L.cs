using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBridge.Core.Localization;

public sealed record Language(string Code, string Name);

/// <summary>
/// Interface language, as in NSA and llm-browser: the same codes, "system" follows the OS and English is
/// the fallback (unsupported language or untranslated text). Keys are the English text itself; each
/// language is Locales/&lt;code&gt;.json (embedded) with { "English text": "Translation" }. {name} is replaced.
/// </summary>
public static partial class L
{
    public const string System = "system";

    public static readonly IReadOnlyList<Language> Languages =
    [
        new("en", "English"),
        new("es", "Español"),
        new("ca", "Català"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("it", "Italiano"),
        new("pt", "Português (Brasil)"),
        new("ru", "Русский"),
        new("ja", "日本語"),
        new("zh", "简体中文"),
        new("tw", "繁體中文"),
    ];

    public static readonly IReadOnlySet<string> Codes = Languages.Select(l => l.Code).ToHashSet();

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache = new();

    /// <summary>The language every <see cref="T"/> call uses. English unless the app sets another.</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    public static event Action? Changed;

    public static void SetLanguage(string code)
    {
        code = Codes.Contains(code) ? code : "en";
        if (code == Current)
            return;
        Current = code;
        Changed?.Invoke();
    }

    /// <summary>System locale (es-ES, zh-Hant-TW, pt-BR...) to one of our codes, or null.</summary>
    public static string? FromLocale(string? locale)
    {
        var l = (locale ?? "").ToLowerInvariant();
        if (TraditionalChinese().IsMatch(l))
            return "tw";
        var baseCode = l.Split('-', '_')[0];
        return Codes.Contains(baseCode) ? baseCode : null;
    }

    /// <param name="setting">"system" (or empty) or a code.</param>
    /// <param name="systemLocales">The system's preferred languages, in order.</param>
    public static string Resolve(string? setting, IEnumerable<string> systemLocales)
    {
        if (setting is not null && Codes.Contains(setting))
            return setting;
        foreach (var locale in systemLocales)
        {
            if (FromLocale(locale) is { } code)
                return code;
        }
        return "en";
    }

    /// <summary>The OS UI languages, most preferred first.</summary>
    public static IEnumerable<string> SystemLocales()
    {
        yield return CultureInfo.CurrentUICulture.Name;
        yield return CultureInfo.InstalledUICulture.Name;
    }

    public static IReadOnlyDictionary<string, string> Dictionary(string code) =>
        code == "en" ? new Dictionary<string, string>() : Cache.GetOrAdd(code, Load);

    private static IReadOnlyDictionary<string, string> Load(string code)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"DeskBridge.Locales.{code}.json");
        if (stream is null)
            return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }

    public static string Format(string text, params (string Name, object? Value)[] vars)
    {
        if (vars.Length == 0)
            return text;
        return Placeholder().Replace(text, m =>
        {
            foreach (var (name, value) in vars)
            {
                if (name == m.Groups[1].Value)
                    return Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
            }
            return m.Value;
        });
    }

    /// <summary>Translates <paramref name="key"/> (English text) into the current language.</summary>
    public static string T(string key, params (string Name, object? Value)[] vars) => TranslateIn(Current, key, vars);

    public static string TranslateIn(string code, string key, params (string Name, object? Value)[] vars)
    {
        var text = Dictionary(code).TryGetValue(key, out var translated) && !string.IsNullOrWhiteSpace(translated) ? translated : key;
        return Format(text, vars);
    }

    /// <summary>The key in English and in every language (to recognise a keyword typed in any of them).</summary>
    public static IEnumerable<string> AllTranslations(string key) =>
        Languages.Select(l => TranslateIn(l.Code, key)).Distinct();

    [GeneratedRegex(@"^zh[-_](hant|tw|hk|mo)")]
    private static partial Regex TraditionalChinese();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();
}
