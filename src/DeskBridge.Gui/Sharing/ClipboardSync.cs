using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;

namespace DeskBridge.Gui.Sharing;

/// <summary>
/// Shares the clipboard (text and images) with the other machines: listens for Windows clipboard
/// changes (WM_CLIPBOARDUPDATE) and hands what was copied to <c>send</c>; puts what other machines
/// copied into this clipboard. What arrives is never sent back (no ping-pong between machines).
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    public const int MaxTextChars = 1_000_000;
    public const int MaxImageBytes = 16 * 1024 * 1024;
    private const int WmClipboardUpdate = 0x031D;

    private readonly HwndSource _source;
    private readonly Func<ClipboardMessage, Task> _send;
    private readonly Action<string> _log;
    private readonly DispatcherTimer _debounce;
    private string? _lastHash;
    private DateTime _ignoreUntil;

    public ClipboardSync(Window window, Func<ClipboardMessage, Task> send, Action<string> log)
    {
        _send = send;
        _log = log;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())!;
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);

        // Apps often write the clipboard in several steps: wait until it settles.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            OnClipboardChanged();
        };
    }

    /// <summary>Whether what is copied here is sent to the other machines.</summary>
    public bool SendEnabled { get; set; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && SendEnabled && DateTime.UtcNow >= _ignoreUntil)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        var message = Read();
        if (message is null)
            return;
        var hash = Hash(message);
        if (hash == _lastHash)
            return; // what another machine just sent us, or no real change
        _lastHash = hash;
        _ = _send(message);
    }

    /// <summary>Puts a clipboard received from another machine here. Runs on the UI thread.</summary>
    public bool Apply(ClipboardMessage message)
    {
        _lastHash = Hash(message);
        _ignoreUntil = DateTime.UtcNow.AddSeconds(2); // our own change must not travel back
        return Retry(() =>
        {
            if (message.Text is { } text)
            {
                Clipboard.SetText(text);
            }
            else if (message.PngBase64 is { } png)
            {
                using var stream = new MemoryStream(Convert.FromBase64String(png));
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Clipboard.SetImage(image);
            }
            return true;
        });
    }

    private ClipboardMessage? Read() => Retry<ClipboardMessage?>(() =>
    {
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (text.Length > MaxTextChars)
            {
                _log(L.T("Clipboard not shared: the text is too long."));
                return null;
            }
            return new ClipboardMessage(text, null);
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length > MaxImageBytes)
            {
                _log(L.T("Clipboard not shared: the image is too large."));
                return null;
            }
            return new ClipboardMessage(null, Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length));
        }
        return null; // files and other formats are not shared through the clipboard
    });

    /// <summary>The clipboard is often briefly locked by the app that just wrote it.</summary>
    private static T? Retry<T>(Func<T> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is COMException or ExternalException && attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is COMException or ExternalException or FormatException or NotSupportedException)
            {
                return default;
            }
        }
    }

    /// <summary>
    /// The same text compares equal however it was written back: Remote Desktop's own clipboard sharing (or
    /// another app) rewrites it with other line endings or a trailing NUL, which used to send it back again.
    /// </summary>
    private static string Hash(ClipboardMessage m) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.Text is null
            ? "img:" + m.PngBase64
            : "txt:" + m.Text.Replace("\r\n", "\n").TrimEnd('\0'))));

    public void Dispose()
    {
        _debounce.Stop();
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
