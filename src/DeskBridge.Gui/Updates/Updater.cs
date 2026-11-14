using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Updates;

namespace DeskBridge.Gui.Updates;

public enum UpdatePhase { Idle, Checking, None, Disabled, Error, Downloading, Ready, Installing }

public sealed record DownloadProgress(double? Percent, long Transferred, long Total, double BytesPerSecond);

/// <summary>
/// Self-update from GitHub Releases, with its own window (<see cref="UpdateWindow"/>), like llm-browser.
/// DeskBridge is a single self-contained DeskBridge.exe: the new one is downloaded next to the user's
/// data, checked against SHA256SUMS.txt from the release, and swapped in on restart (a running exe can
/// be renamed but not overwritten, so the current one becomes DeskBridge.exe.old and is deleted by the
/// next start). Only published builds update; to see the window without publishing anything (local
/// builds only), set DESKBRIDGE_UPDATER_DEMO to a folder with vX.Y.Z*.md files: it simulates a
/// download of the highest version there, with its notes.
/// </summary>
public sealed partial class Updater
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly string DownloadDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBridge", "updates");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Func<bool> _betas;
    private readonly Func<string> _language;
    private readonly Func<Window?> _owner;
    private readonly Action _beforeInstall;
    private readonly string? _demoDir;

    private UpdateWindow? _window;
    private bool _manual;
    private string? _shownFor, _notesFor, _downloadedPath;
    private IReadOnlyList<GitHubRelease> _releases = [];
    private DispatcherTimer? _timer;

    public Updater(Func<bool> betas, Func<string> language, Func<Window?> owner, Action beforeInstall)
    {
        _betas = betas;
        _language = language;
        _owner = owner;
        _beforeInstall = beforeInstall;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskBridge-Updater/{AppInfo.Version}");
        var demo = Environment.GetEnvironmentVariable("DESKBRIDGE_UPDATER_DEMO");
        _demoDir = !AppInfo.IsReleaseBuild && Directory.Exists(demo) ? demo : null;
    }

    public bool Enabled => AppInfo.IsReleaseBuild || _demoDir is not null;

    /// <summary>The update window while it is open (development snapshots).</summary>
    internal Window? OpenWindow => _window;

    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;
    public SemVersion Current { get; } = AppInfo.Version;
    public SemVersion? Version { get; private set; }
    public DownloadProgress? Progress { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<VersionNotes>? Notes { get; private set; }
    public bool NotesFailed { get; private set; }

    /// <summary>Raised on the UI thread after any state change.</summary>
    public event Action? Changed;

    /// <summary>Without news to show (checking, up to date, failed check) the window is just its header.</summary>
    public bool Compact => Phase is UpdatePhase.Idle or UpdatePhase.Checking or UpdatePhase.None or UpdatePhase.Disabled
                           || (Phase == UpdatePhase.Error && Version is null);

    private void Set(UpdatePhase? phase = null, string? error = null, bool clearError = false)
    {
        if (phase is { } p)
            Phase = p;
        if (error is not null || clearError)
            Error = error;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ scheduling

    public void Start()
    {
        if (!Enabled)
            return;
        _timer = new DispatcherTimer { Interval = _demoDir is null ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(1.5) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckEvery;
            if (_demoDir is not null)
                _timer.Stop();
            await CheckAsync(byUser: false);
        };
        _timer.Start();
    }

    public Task CheckNowAsync() => CheckAsync(byUser: true);

    private async Task CheckAsync(bool byUser)
    {
        if (!Enabled)
        {
            Set(UpdatePhase.Disabled);
            Show();
            return;
        }
        if (Phase is UpdatePhase.Downloading or UpdatePhase.Ready or UpdatePhase.Installing or UpdatePhase.Checking)
        {
            // Something is already going on: show it as it is instead of searching again.
            if (byUser)
                Show();
            return;
        }
        if (byUser)
        {
            _manual = true;
            Set(UpdatePhase.Checking, clearError: true);
            Show();
        }

        if (_demoDir is not null)
        {
            await Task.Delay(byUser ? 800 : 0);
            Demo();
            return;
        }

        try
        {
            _releases = ReleaseFeed.Parse(await _http.GetStringAsync(ReleaseFeed.ApiUrl));
            var newest = ReleaseFeed.Newer(_releases, Current, _betas()).FirstOrDefault();
            if (newest is null)
            {
                if (_manual)
                {
                    _manual = false;
                    Set(UpdatePhase.None);
                }
                return;
            }
            await DownloadAsync(newest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            // No network on a background check: stay quiet, it is retried later. If the user asked,
            // or a download was running, say so.
            if (!_manual && Phase != UpdatePhase.Downloading)
            {
                if (Phase == UpdatePhase.Checking)
                    Set(UpdatePhase.Idle);
                return;
            }
            _manual = false;
            Set(UpdatePhase.Error, FirstLine(ex.Message));
            Show();
        }
    }

    // ------------------------------------------------------------------ download

    private async Task DownloadAsync(GitHubRelease release)
    {
        Version = release.Version;
        Progress = null;
        Set(UpdatePhase.Downloading, clearError: true);
        _ = LoadNotesAsync();
        Announce($"{release.Version}:download");
        _manual = false;

        var name = ReleaseFeed.AssetName(release.Version);
        var asset = release.Asset(name)
            ?? throw new InvalidDataException(L.T("Release {version} has no {file}.", ("version", release.Version), ("file", name)));
        var sumsAsset = release.Asset(ReleaseFeed.ChecksumsAsset)
            ?? throw new InvalidDataException(L.T("Release {version} has no {file}.", ("version", release.Version), ("file", ReleaseFeed.ChecksumsAsset)));
        var expected = ReleaseFeed.ExpectedSha256(await _http.GetStringAsync(sumsAsset.DownloadUrl), name)
            ?? throw new InvalidDataException(L.T("{file} does not list {name}.", ("file", ReleaseFeed.ChecksumsAsset), ("name", name)));

        Directory.CreateDirectory(DownloadDir);
        var target = Path.Combine(DownloadDir, name);
        if (!File.Exists(target) || !await HashMatchesAsync(target, expected))
        {
            var partial = target + ".partial";
            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress = p;
                Changed?.Invoke();
            });
            await Task.Run(() => DownloadFileAsync(asset, partial, progress));
            if (!await HashMatchesAsync(partial, expected))
            {
                File.Delete(partial);
                throw new InvalidDataException(L.T("The download is corrupt (checksum mismatch). Try again."));
            }
            File.Move(partial, target, overwrite: true);
        }

        _downloadedPath = target;
        Progress = new DownloadProgress(100, asset.Size, asset.Size, 0);
        Set(UpdatePhase.Ready);
        _ = LoadNotesAsync();
        Announce($"{release.Version}:ready");
    }

    private async Task DownloadFileAsync(ReleaseAsset asset, string path, IProgress<DownloadProgress> progress)
    {
        using var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(path);

        var buffer = new byte[1 << 16];
        long done = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(100))
            {
                lastReport = clock.Elapsed;
                progress.Report(new DownloadProgress(total > 0 ? done * 100.0 / total : null, done, total, done / Math.Max(clock.Elapsed.TotalSeconds, 0.001)));
            }
        }
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected)
    {
        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
        return hash == expected;
    }

    /// <summary>Shown without a manual check only once per version and phase, not every 6 hours.</summary>
    private void Announce(string key)
    {
        if (_manual || _shownFor != key)
        {
            _shownFor = key;
            Show();
        }
    }

    // ------------------------------------------------------------------ notes

    private async Task LoadNotesAsync(bool force = false)
    {
        if (Version is null)
            return;
        var key = $"{Version}|{_language()}|{_betas()}";
        if (!force && _notesFor == key)
            return;
        _notesFor = key;
        Notes = null;
        NotesFailed = false;
        Changed?.Invoke();

        var notes = _demoDir is not null ? LocalNotes(_demoDir, _language()) : await FetchNotesAsync(_language());
        if (_notesFor != key)
            return; // the language or channel changed meanwhile
        Notes = notes;
        NotesFailed = notes is null;
        Changed?.Invoke();
    }

    private async Task<IReadOnlyList<VersionNotes>?> FetchNotesAsync(string language)
    {
        try
        {
            var list = ReleaseFeed.Newer(_releases, Current, _betas());
            var notes = await Task.WhenAll(list.Select(async r =>
            {
                // main first (translations fixed after publishing); a beta not merged yet only has them in its tag.
                var file = ReleaseFeed.NotesFile(r.Version, language);
                var own = language == "en" ? null
                    : await FetchTextAsync($"{ReleaseFeed.RawUrl}/main/{file}") ?? await FetchTextAsync($"{ReleaseFeed.RawUrl}/{r.Tag}/{file}");
                var lines = ReleaseFeed.NoteLines(own ?? ReleaseFeed.BodyBlock(r.Body));
                return new VersionNotes(r.Version, r.Prerelease, lines, language == "en" || (own is not null && lines.Count > 0));
            }));
            return notes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<string?> FetchTextAsync(string url)
    {
        try
        {
            using var response = await _http.GetAsync(url);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>The same from a local folder (demo mode).</summary>
    private IReadOnlyList<VersionNotes> LocalNotes(string dir, string language)
    {
        string? Read(string name)
        {
            var path = Path.Combine(dir, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        return Directory.EnumerateFiles(dir, "v*.md")
            .Select(f => NotesFileName().Match(Path.GetFileName(f)))
            .Where(m => m.Success)
            .Select(m => SemVersion.TryParse(m.Groups[1].Value)!)
            .Distinct()
            .Where(v => v > Current)
            .OrderByDescending(v => v)
            .Select(v =>
            {
                var own = language == "en" ? null : Read($"v{v}.{language}.md");
                return new VersionNotes(v, false, ReleaseFeed.NoteLines(own ?? Read($"v{v}.md")), language == "en" || own is not null);
            })
            .ToList();
    }

    [GeneratedRegex(@"^v(\d+\.\d+\.\d+)(?:\.\w+)?\.md$")]
    private static partial Regex NotesFileName();

    /// <summary>Demo: a ~90 MB download in a few seconds.</summary>
    private void Demo()
    {
        var version = LocalNotes(_demoDir!, "en").FirstOrDefault()?.Version;
        if (version is null)
        {
            Set(UpdatePhase.None);
            return;
        }
        Version = version;
        Progress = null;
        Set(UpdatePhase.Downloading, clearError: true);
        _ = LoadNotesAsync();
        Announce($"{version}:download");
        _manual = false;

        const long total = 92L * 1024 * 1024;
        long done = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            done = Math.Min(total, done + total / 60);
            Progress = new DownloadProgress(done * 100.0 / total, done, total, total / 6.0);
            Changed?.Invoke();
            if (done < total)
                return;
            timer.Stop();
            Set(UpdatePhase.Ready);
            Announce($"{version}:ready");
        };
        timer.Start();
    }

    // ------------------------------------------------------------------ install

    public void RestartNow()
    {
        if (Phase != UpdatePhase.Ready)
            return;
        // The window says the app will close and come back by itself; then it does.
        Set(UpdatePhase.Installing);
        _beforeInstall();
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            if (_demoDir is not null)
                _window?.Close();
            else
                Install(restart: true);
        };
        delay.Start();
    }

    /// <summary>A downloaded update is installed when the app closes, even without "Restart now".</summary>
    public void InstallOnExit()
    {
        if (Phase == UpdatePhase.Ready && _demoDir is null)
            Install(restart: false);
    }

    private void Install(bool restart)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || _downloadedPath is null)
            return;
        var old = exe + ".old";
        try
        {
            if (File.Exists(old))
                File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Copy(_downloadedPath, exe);
            }
            catch
            {
                File.Move(old, exe);
                throw;
            }
            TryDelete(_downloadedPath);
            _downloadedPath = null;

            if (restart)
            {
                Process.Start(new ProcessStartInfo(exe, $"--after-update {Environment.ProcessId}") { UseShellExecute = false });
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!restart)
                return; // closing anyway; it will be offered again next time
            Set(UpdatePhase.Error, L.T("Could not replace {path}: {error} Move DeskBridge to a folder you can write to.",
                ("path", exe), ("error", FirstLine(ex.Message))));
            Show();
        }
    }

    /// <summary>
    /// At startup: after an update, wait for the previous process to exit (it holds the agent's port),
    /// then delete the replaced DeskBridge.exe.old.
    /// </summary>
    public static void CleanUpAfterUpdate(string[] args)
    {
        int i = Array.IndexOf(args, "--after-update");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(15_000);
            }
            catch (ArgumentException)
            {
                // already gone
            }
        }

        if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old"))
        {
            for (int attempt = 0; attempt < 10 && !TryDelete(exe + ".old"); attempt++)
                Thread.Sleep(300);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    // ------------------------------------------------------------------ window and settings

    public void Show()
    {
        if (_window is { IsLoaded: true })
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
            return;
        }
        _window = new UpdateWindow(this) { Owner = _owner() is { IsVisible: true } o ? o : null };
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    public void Retry() => _ = CheckAsync(byUser: true);

    /// <summary>Channel change on the fly: announce again even if that version was already announced.</summary>
    public void BetasChanged()
    {
        _shownFor = null;
        _ = LoadNotesAsync(force: true);
    }

    public void LanguageChanged()
    {
        Changed?.Invoke();
        _ = LoadNotesAsync(force: true);
    }
}
