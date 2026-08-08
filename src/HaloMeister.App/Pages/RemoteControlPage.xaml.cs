using System.Runtime.InteropServices.WindowsRuntime;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;

namespace HaloMeister.App.Pages;

public sealed partial class RemoteControlPage : Page, IActivatablePage
{
    private readonly RemoteControlService _remote = RemoteControlService.Current;
    private readonly RemoteControlFirewallService _firewall = new();
    private bool _busy;
    private int _statusVersion;
    private bool _subscribed;

    public RemoteControlPage()
    {
        InitializeComponent();
    }

    public void OnActivated()
    {
        if (!_subscribed)
        {
            _remote.StateChanged += OnRemoteStateChanged;
            _subscribed = true;
        }

        _ = UpdateStateAsync();
    }

    public void OnDeactivated()
    {
        if (!_subscribed)
            return;
        _remote.StateChanged -= OnRemoteStateChanged;
        _subscribed = false;
    }

    private void OnRemoteStateChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() => _ = UpdateStateAsync());

    private async void OnToggleRemote(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        SetBusy(true);
        try
        {
            if (_remote.Snapshot.IsRunning)
            {
                await _remote.StopAsync();
                ShowStatus(
                    L.Get("remote_control.phone_remote_stopped"),
                    InfoBarSeverity.Success);
            }
            else
            {
                RemoteControlSnapshot snapshot = await _remote.StartAsync();
                if (snapshot.PairingUrl is null)
                {
                    ShowStatus(
                        L.Get("remote_control.no_lan_adapter"),
                        InfoBarSeverity.Warning);
                }
                else
                {
                    ShowStatus(
                        L.Get("remote_control.phone_remote_enabled"),
                        InfoBarSeverity.Success);
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            await UpdateStateAsync();
        }
    }

    private async void OnConfigureFirewall(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        SetBusy(true);
        try
        {
            await _firewall.InstallAsync();
            ShowStatus(
                L.Format("remote_control.firewall_rule_installed", RemoteControlService.Port),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            await UpdateStateAsync();
        }
    }

    private async void OnRemoveFirewall(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        SetBusy(true);
        try
        {
            await _firewall.RemoveAsync();
            ShowStatus(
                L.Get("remote_control.firewall_rule_removed"),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            await UpdateStateAsync();
        }
    }

    private async Task UpdateStateAsync()
    {
        RemoteControlSnapshot snapshot = _remote.Snapshot;
        RemoteFirewallStatus firewall = await _firewall.GetStatusAsync();
        StateText.Text = snapshot.Summary;
        FirewallStatusText.Text = firewall.Summary;
        ConfigureFirewallButton.IsEnabled = !_busy && !firewall.IsCurrent;
        RemoveFirewallButton.IsEnabled = !_busy && firewall.Exists;
        ToggleButton.Content = snapshot.IsRunning
            ? L.Get("remote_control.stop_phone_remote")
            : L.Get("remote_control.enable_remote");
        PairingPanel.Visibility =
            snapshot.PairingUrl is null ? Visibility.Collapsed : Visibility.Visible;
        QrBorder.Visibility =
            snapshot.PairingUrl is null ? Visibility.Collapsed : Visibility.Visible;
        PairingUrlBox.Text = snapshot.PairingUrl ?? "";
        AlternateAddressesText.Text = snapshot.PairingUrls.Count > 1
            ? L.Format(
                "remote_control.other_detected_addresses",
                string.Join("\n", snapshot.PairingUrls.Skip(1)))
            : "";

        if (snapshot.PairingUrl is null)
        {
            PairingQr.Source = null;
            return;
        }

        try
        {
            using QRCodeData data = QRCodeGenerator.GenerateQrCode(
                snapshot.PairingUrl,
                QRCodeGenerator.ECCLevel.Q);
            using var code = new PngByteQRCode(data);
            byte[] png = code.GetGraphic(8);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            PairingQr.Source = bitmap;
        }
        catch (Exception)
        {
            PairingQr.Source = null;
            ShowStatus(
                L.Get("remote_control.qr_code_draw_failed"),
                InfoBarSeverity.Warning);
        }
    }

    private void OnCopyAddress(object sender, RoutedEventArgs e)
    {
        if (_remote.Snapshot.PairingUrl is not string address)
            return;
        var package = new DataPackage();
        package.SetText(address);
        Clipboard.SetContent(package);
        ShowStatus(L.Get("remote_control.pairing_address_copied"), InfoBarSeverity.Success);
    }

    private async void OnOpenLocally(object sender, RoutedEventArgs e)
    {
        if (_remote.Snapshot.PairingUrl is not string address)
            return;
        try
        {
            await Launcher.LaunchUriAsync(new Uri(address));
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        int version = ++_statusVersion;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
        if (severity == InfoBarSeverity.Success)
            _ = DismissSuccessAsync(version);
    }

    private async Task DismissSuccessAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (version == _statusVersion && StatusBar.Severity == InfoBarSeverity.Success)
            StatusBar.IsOpen = false;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        ToggleButton.IsEnabled = !busy;
        ConfigureFirewallButton.IsEnabled = !busy;
        RemoveFirewallButton.IsEnabled = !busy;
    }
}
