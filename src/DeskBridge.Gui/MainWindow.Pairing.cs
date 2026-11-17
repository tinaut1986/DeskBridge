using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;

namespace DeskBridge.Gui;

/// <summary>
/// Joining a phone or tablet without typing the shared key on it: a QR code with a deskbridge://join link (the
/// coordinator's address and the key). Scanned with the phone's camera, or with Scan QR in the Android app, it
/// opens the app with everything filled in.
/// </summary>
public partial class MainWindow
{
    private void PhoneQr_Click(object sender, RoutedEventArgs e)
    {
        var key = _vm.LocalKey.Trim();
        try
        {
            ControlAuth.DecodeKey(key);
        }
        catch (ControlException)
        {
            _vm.Status = L.T("Generate or paste a shared key first.");
            return;
        }
        if (CoordinatorAddress() is not { } address)
        {
            _vm.Status = L.T("This PC has no network address the phone could reach.");
            return;
        }
        var link = $"deskbridge://join?c={Uri.EscapeDataString(address)}&k={Uri.EscapeDataString(key)}";
        ShowQr(link, address);
    }

    /// <summary>
    /// Where the phone connects: the coordinator this PC connects to, or this PC (its address on the local network,
    /// the one its default route goes out through, and its port).
    /// </summary>
    private string? CoordinatorAddress()
    {
        if (IsClientHere())
            return _vm.LocalConnectTo.Trim();
        const int defaultPort = 47900; // what the phone assumes when the address has none
        var port = int.TryParse(_vm.LocalPort, out var p) && p is > 0 and < 65536 ? p : defaultPort;
        return LocalAddress() is { } ip ? (port == defaultPort ? ip.ToString() : $"{ip}:{port}") : null;
    }

    private static IPAddress? LocalAddress()
    {
        try
        {
            // A UDP "connection" sends nothing: it only asks Windows which address the route out would use.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(IPAddress.Parse("192.0.2.1"), 9);
            if (socket.LocalEndPoint is IPEndPoint { Address: var address } && !IPAddress.IsLoopback(address))
                return address;
        }
        catch (SocketException)
        {
        }
        // No default route: the first network adapter that is up and not virtual.
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel
                        && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        && !n.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
    }

    private void ShowQr(string link, string address)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(link, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();

        var close = new Button { Content = L.T("Close"), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, IsDefault = true };
        var panel = new StackPanel { Margin = new Thickness(24), MaxWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Connect a phone or tablet"), FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.Resource("NormalText"), Margin = new Thickness(0, 0, 0, 12),
        });
        // Black on white whatever the theme: what cameras read best.
        panel.Children.Add(new Border
        {
            Background = System.Windows.Media.Brushes.White, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image { Source = image, Width = 300, Height = 300 },
        });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Scan it with the camera of the phone or tablet, or with Scan QR in the DeskBridge app: the app opens with this PC's address and the shared key filled in."),
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Resource("NormalText"), Margin = new Thickness(0, 12, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("It holds the shared key: show it only to your own devices. Address: {address}", ("address", address)),
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Resource("MutedText"), Margin = new Thickness(0, 0, 0, 16),
        });
        panel.Children.Add(close);

        var window = new Window
        {
            Title = L.T("Connect a phone or tablet"),
            Content = panel,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = ThemeManager.Resource("AppBackground"),
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }
}
