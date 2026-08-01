using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class ArmorMixerPage : Page
{
    private readonly AppState _state = AppState.Current;
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ArmorMixerService _mixer = new();
    private ArmorMixerSession? _session;
    private IReadOnlyList<ArmorMixerRegionRow> _rows = [];
    private bool _busy;
    private bool _changingBase;

    public ArmorMixerPage()
    {
        InitializeComponent();
        _game.ConnectionChanged += OnGameConnectionChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        OwnershipNotice.Text = BuildPolicy.EnforceCustomizationOwnership
            ? L.Get("armor_mixer.retail_ownership_notice")
            : L.Get("armor_mixer.developer_build_notice");
        UpdateChrome();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_game.IsConnected && _session is null)
            await ScanAsync(connectGame: false);
    }

    private async void OnConnectAndScan(object sender, RoutedEventArgs e)
        => await ScanAsync(connectGame: false);

    private async Task ScanAsync(bool connectGame)
    {
        await RunBusy(async () =>
        {
            if (connectGame && !_game.IsConnected)
                await Task.Run(_game.Connect);

            ArmorMixerSession scanned = await Task.Run(_mixer.Scan);
            ArmorMixerVariant[] available = scanned.Variants
                .Where(CanUsePlayerVariant)
                .ToArray();
            if (available.Length == 0)
                throw new InvalidOperationException(
                    L.Get("armor_mixer.no_variants_passed_ownership"));
            ArmorMixerSession session = scanned with { Variants = available };
            _session = session;
            _changingBase = true;
            BaseVariantCombo.ItemsSource = session.Variants;
            BaseVariantCombo.SelectedItem =
                session.Variants.FirstOrDefault(variant => variant.Index == 0) ??
                session.Variants[0];
            _changingBase = false;
            RebuildRows();
            ShowStatus(
                BuildPolicy.EnforceCustomizationOwnership
                    ? L.Format(
                        "armor_mixer.scan_found_owned",
                        session.Variants.Count.ToString("N0"),
                        (scanned.Variants.Count - session.Variants.Count).ToString("N0"))
                    : L.Format(
                        "armor_mixer.scan_found_appearances",
                        session.Variants.Count.ToString("N0")),
                InfoBarSeverity.Success);
        });
    }

    private void OnBaseVariantChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_changingBase)
            RebuildRows();
    }

    private void RebuildRows()
    {
        if (_session is null ||
            BaseVariantCombo.SelectedItem is not ArmorMixerVariant baseVariant)
        {
            _rows = [];
            RegionList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Visible;
            EditorGrid.Visibility = Visibility.Collapsed;
            UpdateChrome();
            return;
        }

        _rows = baseVariant.Regions
            .Select(region =>
            {
                IEnumerable<ArmorMixerVariant> candidates = _session.Variants;
                ArmorMixerVariant[] donors = candidates
                    .Where(variant => variant.Regions.Any(candidate =>
                        candidate.NameStringId == region.NameStringId))
                    .ToArray();
                ArmorMixerVariant selected = donors.FirstOrDefault(variant =>
                    variant.Index == baseVariant.Index) ?? donors[0];
                return new ArmorMixerRegionRow(region, donors, selected);
            })
            .ToArray();
        RegionList.ItemsSource = _rows;
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        EditorGrid.Visibility = Visibility.Visible;
        BaseDetailText.Text = baseVariant.Detail;
        RegionCountText.Text = L.Format("armor_mixer.region_count", _rows.Count.ToString("N0"));
        SummaryText.Text = L.Format(
            "armor_mixer.appearances_regions_summary",
            _session.Variants.Count.ToString("N0"),
            _rows.Count.ToString("N0"));
        UpdateChrome();
    }

    private void OnResetRows(object sender, RoutedEventArgs e)
    {
        if (BaseVariantCombo.SelectedItem is not ArmorMixerVariant baseVariant)
            return;

        ArmorMixerVariant? blocked = _rows
            .Select(row => row.SelectedDonor)
            .Prepend(baseVariant)
            .FirstOrDefault(variant => !CanUsePlayerVariant(variant));
        if (blocked is not null)
        {
            ShowStatus(
                L.Format("armor_mixer.cannot_apply_ownership", blocked.DisplayName),
                InfoBarSeverity.Warning);
            return;
        }
        foreach (ArmorMixerRegionRow row in _rows)
        {
            row.SelectedDonor = row.Donors.FirstOrDefault(variant =>
                variant.Index == baseVariant.Index) ?? row.Donors[0];
        }
        RegionList.ItemsSource = null;
        RegionList.ItemsSource = _rows;
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (BaseVariantCombo.SelectedItem is not ArmorMixerVariant baseVariant)
            return;

        await RunBusy(async () =>
        {
            ArmorMixerSelection[] selections = _rows
                .Select(row => new ArmorMixerSelection(
                    row.Region,
                    row.SelectedDonor))
                .ToArray();
            ArmorMixerApplyResult result =
                await _mixer.ApplyAsync(
                    baseVariant,
                    selections);
            ShowStatus(
                L.Format(
                    "armor_mixer.applied_mixed_armor",
                    result.BaseVariant,
                    result.MixedRegionCount.ToString("N0")),
                InfoBarSeverity.Success);
            SummaryText.Text = result.RuntimeMessage;
        });
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateChrome();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateChrome();
        }
    }

    private void OnGameConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateChrome);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _game.ConnectionChanged -= OnGameConnectionChanged;
    }

    private void UpdateChrome()
    {
        ScriptingBridgeStatus bridge = _mixer.BridgeStatus;
        bool bridgeReady = bridge.IsRuntimeReady && !bridge.IsStale;
        ConnectionStatusText.Text = _mixer.ConnectionSummary;
        BridgeStatusText.Text = bridgeReady
            ? L.Format("armor_mixer.bridge_ready_version", bridge.RunningVersion)
            : bridge.Summary;
        ScanButton.Content = _session is null
            ? L.Get("armor_mixer.scan_armor")
            : L.Get("armor_mixer.rescan");
        ScanButton.IsEnabled = !_busy && _game.IsConnected;
        BaseVariantCombo.IsEnabled = !_busy && _session is not null;
        RegionList.IsEnabled = !_busy && _rows.Count > 0;
        ResetRowsButton.IsEnabled = !_busy && _rows.Count > 0;
        ApplyButton.IsEnabled =
            !_busy &&
            _game.IsConnected &&
            bridgeReady &&
            BaseVariantCombo.SelectedItem is ArmorMixerVariant &&
            _rows.Count > 0;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (severity == InfoBarSeverity.Success)
        {
            StatusBar.IsOpen = false;
            SummaryText.Text = message;
            return;
        }

        StatusBar.Title = severity switch
        {
            InfoBarSeverity.Error => L.Get("armor_mixer.failed"),
            InfoBarSeverity.Success => L.Get("armor_mixer.armor_mixer"),
            _ => L.Get("armor_mixer.armor_mixer"),
        };
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private bool CanUsePlayerVariant(ArmorMixerVariant variant)
    {
        if (!BuildPolicy.EnforceCustomizationOwnership) return true;

        CosmeticChoice? choice =
            CustomizationCatalog.FindMasterChiefChoiceForVariantIndex(variant.Index);
        return choice is not null &&
            CustomizationCatalog.CanEquipInRetail(choice, _state.Entitlements);
    }

    private sealed class ArmorMixerRegionRow
    {
        public ArmorMixerRegionRow(
            ArmorMixerRegion region,
            IReadOnlyList<ArmorMixerVariant> donors,
            ArmorMixerVariant selectedDonor)
        {
            Region = region;
            Donors = donors;
            SelectedDonor = selectedDonor;
        }

        public ArmorMixerRegion Region { get; }
        public string DisplayName => Region.DisplayName;
        public string Detail => Region.Detail;
        public IReadOnlyList<ArmorMixerVariant> Donors { get; }
        public ArmorMixerVariant SelectedDonor { get; set; }
    }
}
