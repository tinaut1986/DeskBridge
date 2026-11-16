using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBridge.Core.Updates;

/// <summary>X.Y.Z, the only tag shape the release workflow publishes (vX.Y.Z).</summary>
public sealed partial record SemVersion(int Major, int Minor, int Patch) : IComparable<SemVersion>
{
    public static SemVersion? TryParse(string? text)
    {
        var m = Pattern().Match(text ?? "");
        return m.Success ? new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    public int CompareTo(SemVersion? other) =>
        other is null ? 1 : (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$")]
    private static partial Regex Pattern();
}

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record GitHubRelease(string Tag, SemVersion Version, bool Prerelease, string Body, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => a.Name == name);
}

/// <summary>What's new in one version, in the interface language when there is a file for it.</summary>
public sealed record VersionNotes(SemVersion Version, bool Beta, IReadOnlyList<string> Lines, bool Translated);

/// <summary>
/// GitHub Releases as the update source, as in llm-browser: a vX.Y.Z tag on main is a stable release,
/// on an unmerged release/* branch a beta (pre-release). Notes per version and language live in
/// docs/release-notes/vX.Y.Z[.code].md; the English ones are also in the release body between markers.
/// </summary>
public static partial class ReleaseFeed
{
    public const string Owner = "tinaut1986";
    public const string Repo = "DeskBridge";
    public const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";
    public const string ProjectPage = $"https://github.com/{Owner}/{Repo}";
    public const string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";
    public const string RawUrl = $"https://raw.githubusercontent.com/{Owner}/{Repo}";
    public const string ChecksumsAsset = "SHA256SUMS.txt";
    public const string NotesStart = "<!-- deskbridge-notes -->";
    public const string NotesEnd = "<!-- /deskbridge-notes -->";

    public static string AssetName(SemVersion version) => $"DeskBridge-{version}-win-x64.exe";

    /// <summary>Releases from the GitHub API response; drafts and tags that are not vX.Y.Z are skipped.</summary>
    public static IReadOnlyList<GitHubRelease> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<GitHubRelease>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (Bool(r, "draft"))
                continue;
            var tag = Str(r, "tag_name");
            if (SemVersion.TryParse(tag) is not { } version || !tag.StartsWith('v') || tag.Contains('-'))
                continue;
            var assets = r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => new ReleaseAsset(Str(x, "name"), Str(x, "browser_download_url"),
                    x.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0)).ToList()
                : [];
            result.Add(new GitHubRelease(tag, version, Bool(r, "prerelease"), Str(r, "body"), assets));
        }
        return result;

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Releases of the channel newer than <paramref name="current"/>, newest first. Stable: only stable
    /// ones (their notes already cover the betas in between). With betas: all of them.
    /// </summary>
    public static IReadOnlyList<GitHubRelease> Newer(IEnumerable<GitHubRelease> releases, SemVersion current, bool betas) =>
        releases.Where(r => (betas || !r.Prerelease) && r.Version > current)
            .OrderByDescending(r => r.Version)
            .ToList();

    /// <summary>"- ..." lines of some notes without Markdown marks; if there are no bullets, the text lines.</summary>
    public static IReadOnlyList<string> NoteLines(string? markdown)
    {
        var lines = (markdown ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var bullets = lines.Where(l => l.StartsWith("- ") || l.StartsWith("* ")).Select(l => l[2..]).ToList();
        return (bullets.Count > 0 ? bullets : lines.Where(l => !l.StartsWith('#')).ToList())
            .Select(l => MarkdownMarks().Replace(l, "").Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    /// <summary>The English notes block of a release body (between markers, or after "## Changelog").</summary>
    public static string BodyBlock(string? body)
    {
        body ??= "";
        int start = body.IndexOf(NotesStart, StringComparison.Ordinal);
        int end = start < 0 ? -1 : body.IndexOf(NotesEnd, start, StringComparison.Ordinal);
        if (start >= 0 && end > start)
            return body[(start + NotesStart.Length)..end];
        int changelog = body.IndexOf("## Changelog", StringComparison.Ordinal);
        return changelog >= 0 ? body[(changelog + "## Changelog".Length)..] : "";
    }

    /// <summary>Path of a notes file in the repository: vX.Y.Z.md (English) or vX.Y.Z.code.md.</summary>
    public static string NotesFile(SemVersion version, string language) =>
        language == "en" ? $"docs/release-notes/v{version}.md" : $"docs/release-notes/v{version}.{language}.md";

    /// <summary>The expected hash of <paramref name="fileName"/> in a sha256sum-style file ("hash  name").</summary>
    public static string? ExpectedSha256(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var parts = raw.Trim().Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim() == fileName && Sha256Hex().IsMatch(parts[0]))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    [GeneratedRegex(@"[*`]")]
    private static partial Regex MarkdownMarks();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();
}
