#:property PublishAot=false
// Lists the interface texts that go through the translator: L.T("...") in src/**/*.cs and
// {l:T '...'} in src/**/*.xaml. Without arguments it prints every key (one JSON string per line);
// with --missing <code>, only those missing from src/DeskBridge.Core/Locales/<code>.json.
// Same rules as the I18nTests scanner. Run from the repository root:
//   dotnet run scripts/i18n-keys.cs -- --missing es
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

var root = Directory.GetCurrentDirectory();
var src = Path.Combine(root, "src");
if (!Directory.Exists(src))
{
    Console.Error.WriteLine("Run it from the repository root.");
    return 1;
}

var keys = new SortedSet<string>(StringComparer.Ordinal);
var codeKey = new Regex("""(?<![\w.])L\.T\(\s*"((?:[^"\\]|\\.)*)"(?=\s*[,)])""");
var xamlKey = new Regex(@"\{l:T\s+'([^']*)'\}");
var sep = Path.DirectorySeparatorChar;
foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
{
    if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}"))
        continue;
    if (file.EndsWith(".cs"))
        foreach (Match m in codeKey.Matches(File.ReadAllText(file)))
            keys.Add(Regex.Unescape(m.Groups[1].Value));
    else if (file.EndsWith(".xaml"))
        foreach (Match m in xamlKey.Matches(File.ReadAllText(file)))
            keys.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
}

IEnumerable<string> list = keys;
int i = Array.IndexOf(args, "--missing");
if (i >= 0 && i + 1 < args.Length)
{
    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(
        File.ReadAllText(Path.Combine(src, "DeskBridge.Core", "Locales", args[i + 1] + ".json")))!;
    list = keys.Where(k => !dict.ContainsKey(k));
}

var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
foreach (var key in list)
    Console.WriteLine(JsonSerializer.Serialize(key, options));
return 0;
