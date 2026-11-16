using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBridge.Core.Localization;

namespace DeskBridge.Core.Tests;

/// <summary>The interface texts that go through the translator, found in the sources like scripts/i18n-keys.cs does.</summary>
internal static partial class I18nKeys
{
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DeskBridge.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("DeskBridge.slnx not found above the test output.");
    }

    public static SortedSet<string> Scan()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            if (file.EndsWith(".cs", StringComparison.Ordinal))
            {
                foreach (Match m in CodeKey().Matches(File.ReadAllText(file)))
                    keys.Add(Regex.Unescape(m.Groups[1].Value));
            }
            else if (file.EndsWith(".xaml", StringComparison.Ordinal))
            {
                foreach (Match m in XamlKey().Matches(File.ReadAllText(file)))
                    keys.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
            }
        }
        return keys;
    }

    public static Dictionary<string, string> Locale(string code) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "DeskBridge.Core", "Locales", code + ".json")))!;

    [GeneratedRegex("""(?<![\w.])L\.T\(\s*"((?:[^"\\]|\\.)*)"(?=\s*[,)])""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{l:T\s+'([^']*)'\}")]
    private static partial Regex XamlKey();
}

public class I18nTests
{
    private static List<string> Placeholders(string s) =>
        Regex.Matches(s, @"\{(\w+)\}").Select(m => m.Groups[1].Value).Order().ToList();

    [Fact]
    public void Every_language_has_every_text_with_the_same_placeholders()
    {
        var all = I18nKeys.Scan();
        Assert.True(all.Count > 100, $"the key scan found the interface texts ({all.Count})");

        var problems = new List<string>();
        foreach (var language in L.Languages.Where(l => l.Code != "en"))
        {
            var dict = I18nKeys.Locale(language.Code);
            foreach (var key in all)
            {
                if (!dict.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                    problems.Add($"{language.Code}: missing {JsonSerializer.Serialize(key)}");
                else if (!Placeholders(value).SequenceEqual(Placeholders(key)))
                    problems.Add($"{language.Code}: placeholders of {JsonSerializer.Serialize(key)}");
            }
            foreach (var key in dict.Keys.Where(k => !all.Contains(k)))
                problems.Add($"{language.Code}: {JsonSerializer.Serialize(key)} is no longer used");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(40)) + (problems.Count > 40 ? $"{Environment.NewLine}... and {problems.Count - 40} more" : ""));
    }

    [Fact]
    public void Embedded_dictionaries_match_the_files()
    {
        foreach (var language in L.Languages.Where(l => l.Code != "en"))
            Assert.Equal(I18nKeys.Locale(language.Code).Count, L.Dictionary(language.Code).Count);
    }

    [Fact]
    public void System_locales_map_onto_the_NSA_language_codes_with_English_as_fallback()
    {
        Assert.Equal("es", L.Resolve("system", ["es-ES"]));
        Assert.Equal("pt", L.Resolve("system", ["pt-BR"]));
        Assert.Equal("zh", L.Resolve("system", ["zh-CN"]));
        Assert.Equal("tw", L.Resolve("system", ["zh-Hant-TW"]));
        Assert.Equal("tw", L.Resolve("system", ["zh-HK"]));
        Assert.Equal("ca", L.Resolve("system", ["nl-NL", "ca-ES"]));
        Assert.Equal("en", L.Resolve("system", ["nl-NL"]));
        Assert.Equal("en", L.Resolve(null, []));
        Assert.Equal("fr", L.Resolve("fr", ["es-ES"]));
        Assert.Equal("de", L.Resolve("xx", ["de-DE"]));
    }

    [Fact]
    public void Translator_fills_placeholders_and_falls_back_to_the_English_text()
    {
        Assert.Equal("Versión instalada: 1.2.3", L.TranslateIn("es", "Installed version: {version}", ("version", "1.2.3")));
        Assert.Equal("Some text nobody translated", L.TranslateIn("es", "Some text nobody translated"));
        Assert.Equal("1 of 2 MB", L.TranslateIn("en", "{done} of {total} MB", ("done", 1), ("total", 2)));
        Assert.Equal("{unknown} stays", L.Format("{unknown} stays", ("other", 1)));
    }
}
