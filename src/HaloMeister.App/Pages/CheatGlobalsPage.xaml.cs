using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class CheatGlobalsPage : Page, IActivatablePage
{
    private sealed record OtherFeatureDefinition(
        string TitleKey,
        string DescriptionKey,
        string ActionKey,
        string Script,
        string SuccessKey);

    private sealed class OtherFeatureItem : INotifyPropertyChanged
    {
        public required string Title { get; init; }
        public required string Description { get; init; }
        public required string ActionLabel { get; init; }
        public required string Script { get; init; }
        public required string SuccessKey { get; init; }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static readonly OtherFeatureDefinition[] OtherFeatureDefinitions =
    [
        new(
            "other_gameplay.hide_hud",
            "other_gameplay.hide_hud_desc",
            "other_gameplay.apply",
            "hs:chud_show 0",
            "other_gameplay.hide_hud_submitted"),
        new(
            "other_gameplay.show_hud",
            "other_gameplay.show_hud_desc",
            "other_gameplay.apply",
            "hs:chud_show 1",
            "other_gameplay.show_hud_submitted"),
    ];

    private readonly CheatGlobalsService _cheats = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly List<OtherFeatureItem> _otherFeatures = [];
    private readonly PlayerModifiersService _modifiers =
        PlayerModifiersService.Current;
    private readonly PlayerTeamService _playerTeam = new();
    private readonly AllegianceDemoService _globalAllegiance = new();
    private readonly SuperPunchService _superPunch = SuperPunchService.Current;
    private readonly WeaponActionTimingService _actionTiming =
        WeaponActionTimingService.Current;
    private IReadOnlyList<CheatGlobalItem> _items = [];
    private IReadOnlyList<PlayerModifierItem> _modifierItems = [];
    private readonly Dictionary<string, bool> _loaded =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerModifierOption> _loadedModifiers =
        new(StringComparer.Ordinal);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _busy;
    private bool _updatingActionTimingToggle;
    private bool _loading;
    private bool _cheatsLive;
    private bool _traitsLive;
    private bool _cheatReadInFlight;
    private bool _traitReadInFlight;
    private bool _loadingModifiers;
    private bool _loadingTeam;
    private PlayerTeamState? _teamState;
    private string _section = "quick-cheats";

    public CheatGlobalsPage()
    {
        InitializeComponent();
        foreach (OtherFeatureDefinition definition in OtherFeatureDefinitions)
        {
            _otherFeatures.Add(new OtherFeatureItem
            {
                Title = L.Get(definition.TitleKey),
                Description = L.Get(definition.DescriptionKey),
                ActionLabel = L.Get(definition.ActionKey),
                Script = definition.Script,
                SuccessKey = definition.SuccessKey,
            });
        }

        OtherFeaturesList.ItemsSource = _otherFeatures;
        PlayerTeamComboBox.ItemsSource = PlayerTeamService.Options;
        // Global ai_allegiance pairs player with a concrete campaign team.
        PlayerTeamOption[] globalTeams = PlayerTeamService.Options
            .Where(option => option.Value > 0)
            .ToArray();
        GlobalAllegianceTeamComboBox.ItemsSource = globalTeams;
        GlobalAllegianceTeamComboBox.SelectedItem = globalTeams.FirstOrDefault(
            option => option.Value == AllegianceDemoService.HostileTeam)
            ?? globalTeams.FirstOrDefault();
        BindCheats(CreateCheatPlaceholders(), live: false);
        BindTraits(CreateTraitPlaceholders(), live: false);
        _statusTimer.Tick += OnStatusTimer;
        ShowSection(_section);
        SyncWeaponInterruptionToggle();
        UpdateBridgeStatus();
        UpdateButtons();
    }

    public void ShowSection(string section)
    {
        _section = section == "allegiance" ? "allegiance" : "quick-cheats";

        QuickCheatsPanel.Visibility =
            _section == "quick-cheats" ? Visibility.Visible : Visibility.Collapsed;
        AllegiancePanel.Visibility =
            _section == "allegiance" ? Visibility.Visible : Visibility.Collapsed;

        UpdateSummary();
        UpdateButtons();
    }

    public void OnActivated()
    {
        SyncWeaponInterruptionToggle();
        UpdateBridgeStatus();
        UpdateButtons();
        _statusTimer.Start();
        _ = RefreshLiveStateAsync();
    }

    public void OnDeactivated() => _statusTimer.Stop();

    private void OnStatusTimer(object? sender, object e)
    {
        UpdateBridgeStatus();
        if (!_cheats.BridgeStatus.IsRuntimeReady || _cheats.BridgeStatus.IsStale)
            _cheatsLive = false;
        if (!RuntimeTagMemoryService.Current.IsConnected)
            _traitsLive = false;
        UpdateSummary();
        UpdateButtons();
        _ = RefreshLiveStateAsync();
    }

    private async Task RefreshLiveStateAsync()
    {
        await TryReadCheatsAsync(report: false);
        await TryReadTraitsAsync(report: false);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            IReadOnlyList<CheatGlobalItem> items = await _cheats.ReadAsync();
            BindCheats(items, live: true);
            ShowStatus(
                L.Format("cheat_globals.read_all_cheats", items.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnDisableAll(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            CheatGlobalItem[] enabled = _items
                .Where(item => item.IsEnabled)
                .ToArray();
            foreach (CheatGlobalItem item in enabled)
            {
                await _cheats.SetAsync(item.Name, false);
                item.IsEnabled = false;
                _loaded[item.Name] = false;
            }
            BindCheats(await _cheats.ReadAsync(), live: true);
            ShowStatus(
                enabled.Length == 0
                    ? L.Get("cheat_globals.all_cheats_already_off")
                    : L.Format("cheat_globals.turned_off_cheats", enabled.Length),
                InfoBarSeverity.Success);
        });
    }

    private async void OnGlobalToggled(object sender, RoutedEventArgs e)
    {
        if (_loading ||
            _busy ||
            sender is not ToggleSwitch toggle ||
            toggle.DataContext is not CheatGlobalItem item ||
            !_loaded.TryGetValue(item.Name, out bool previous) ||
            previous == toggle.IsOn)
        {
            return;
        }

        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            await _cheats.SetAsync(item.Name, toggle.IsOn);
            _loaded[item.Name] = toggle.IsOn;
            item.IsEnabled = toggle.IsOn;
            UpdateSummary();
            ShowStatus(
                L.Format(
                    "cheat_globals.cheat_now_on_off",
                    item.DisplayName,
                    L.Get(toggle.IsOn ? "cheat_globals.on" : "cheat_globals.off")),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _loading = true;
            toggle.IsOn = previous;
            item.IsEnabled = previous;
            _loading = false;
            ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private async void OnRefreshModifiers(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            IReadOnlyList<PlayerModifierItem> items =
                await Task.Run(_modifiers.Read);
            BindTraits(items, live: true);
            ShowStatus(
                L.Format("cheat_globals.loaded_modifiers", items.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRestoreModifiers(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            int restored = await Task.Run(_modifiers.Restore);
            BindTraits(await Task.Run(_modifiers.Read), live: true);
            ShowStatus(
                restored == 0
                    ? L.Get("cheat_globals.no_modifiers_to_restore")
                    : L.Format("cheat_globals.restored_modifiers", restored),
                InfoBarSeverity.Success);
        });
    }

    private async void OnModifierChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingModifiers ||
            _busy ||
            sender is not ComboBox combo ||
            combo.DataContext is not PlayerModifierItem item ||
            combo.SelectedItem is not PlayerModifierOption selected ||
            !_loadedModifiers.TryGetValue(
                item.Name,
                out PlayerModifierOption? previous) ||
            previous.Value == selected.Value)
        {
            return;
        }

        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            await Task.Run(() =>
                _modifiers.Set(item.Name, selected.Value));
            _loadedModifiers[item.Name] = selected;
            item.SelectedOption = selected;
            UpdateSummary();
            ShowStatus(
                L.Format("cheat_globals.modifier_now_value", item.DisplayName, selected.Label),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _loadingModifiers = true;
            combo.SelectedItem = previous;
            item.SelectedOption = previous;
            _loadingModifiers = false;
            ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private async void OnLoadPlayerTeam(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            ShowPlayerTeam(await _playerTeam.ReadAsync());
            ShowStatus(
                L.Get("cheat_globals.loaded_player_team"),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRestorePlayerTeam(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            ShowPlayerTeam(await _playerTeam.RestoreAsync());
            ShowStatus(
                L.Get("cheat_globals.restored_player_team"),
                InfoBarSeverity.Success);
        });
    }

    private async void OnGlobalAllegianceAlly(object sender, RoutedEventArgs e) =>
        await SubmitGlobalAllegianceAsync(breakAllegiance: false);

    private async void OnGlobalAllegianceBreak(object sender, RoutedEventArgs e) =>
        await SubmitGlobalAllegianceAsync(breakAllegiance: true);

    private async Task SubmitGlobalAllegianceAsync(bool breakAllegiance)
    {
        if (GlobalAllegianceTeamComboBox.SelectedItem is not PlayerTeamOption team)
            return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _globalAllegiance.SubmitAllegianceAsync(
                team.Value,
                breakAllegiance);
            string verb = breakAllegiance
                ? L.Format(
                    "cheat_globals.global_allegiance_break_ok",
                    AllegianceDemoService.HaloScriptTeamName(team.Value))
                : L.Format(
                    "cheat_globals.global_allegiance_submit_ok",
                    AllegianceDemoService.HaloScriptTeamName(team.Value));
            if (result.Outcome == ScriptOutcome.Failed)
            {
                ShowStatus(UserFacingErrors.FromBridge(result.Message), InfoBarSeverity.Error);
                return;
            }
            // result.Message echoes the submitted HaloScript; keep it out of the UI.
            ShowStatus(
                verb,
                result.Outcome == ScriptOutcome.Confirmed
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Informational);
        });
    }

    private async void OnPlayerTeamChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingTeam ||
            _busy ||
            _teamState is null ||
            sender is not ComboBox combo ||
            combo.SelectedItem is not PlayerTeamOption selected ||
            selected.Value == _teamState.Selected.Value)
        {
            return;
        }

        if (selected.Value < 0)
        {
            if (_teamState.HasSnapshot)
            {
                await RunBusy(async () =>
                {
                    ShowPlayerTeam(await _playerTeam.RestoreAsync());
                    ShowStatus(
                        L.Get("cheat_globals.restored_player_team"),
                        InfoBarSeverity.Success);
                });
            }
            else
            {
                _loadingTeam = true;
                combo.SelectedItem = _teamState.Selected;
                _loadingTeam = false;
                PlayerTeamDescriptionText.Text = _teamState.Selected.Description;
            }
            return;
        }

        PlayerTeamOption previous = _teamState.Selected;
        PlayerTeamDescriptionText.Text = selected.Description;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            ShowPlayerTeam(await _playerTeam.SetAsync(selected.Value));
            ShowStatus(
                L.Format("cheat_globals.allegiance_now", selected.Label),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _loadingTeam = true;
            combo.SelectedItem = previous;
            _loadingTeam = false;
            PlayerTeamDescriptionText.Text = previous.Description;
            ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private static IReadOnlyList<CheatGlobalItem> CreateCheatPlaceholders() =>
        CheatGlobalsService.Catalog.Select(definition => new CheatGlobalItem
        {
            Definition = definition,
            IsEnabled = false,
        }).ToArray();

    private static IReadOnlyList<PlayerModifierItem> CreateTraitPlaceholders() =>
        PlayerModifiersService.Catalog.Select(definition => new PlayerModifierItem
        {
            Definition = definition,
            SelectedOption = definition.Options[0],
        }).ToArray();

    private void BindCheats(IReadOnlyList<CheatGlobalItem> items, bool live)
    {
        _loading = true;
        _items = items;
        _cheatsLive = live;
        _loaded.Clear();
        if (live)
        {
            foreach (CheatGlobalItem item in items)
                _loaded[item.Name] = item.IsEnabled;
        }

        GlobalsList.ItemsSource = items;
        _loading = false;
        UpdateSummary();
        UpdateButtons();
    }

    private void BindTraits(IReadOnlyList<PlayerModifierItem> items, bool live)
    {
        _loadingModifiers = true;
        _modifierItems = items;
        _traitsLive = live;
        _loadedModifiers.Clear();
        if (live)
        {
            foreach (PlayerModifierItem item in items)
                _loadedModifiers[item.Name] = item.SelectedOption;
        }

        ModifierList.ItemsSource = items;
        _loadingModifiers = false;
        UpdateSummary();
        UpdateButtons();
    }

    private async Task TryReadCheatsAsync(bool report)
    {
        if (_busy || _cheatReadInFlight)
            return;
        ScriptingBridgeStatus bridge = _cheats.BridgeStatus;
        bool ready = bridge.IsRuntimeReady && !bridge.IsStale;
        if (!ready || (_cheatsLive && !report))
            return;

        _cheatReadInFlight = true;
        try
        {
            IReadOnlyList<CheatGlobalItem> items = await _cheats.ReadAsync();
            BindCheats(items, live: true);
            if (report)
            {
                ShowStatus(
                    L.Format("cheat_globals.read_all_cheats", items.Count),
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            _cheatsLive = false;
            UpdateButtons();
            if (report)
                ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
        finally
        {
            _cheatReadInFlight = false;
        }
    }

    private async Task TryReadTraitsAsync(bool report)
    {
        if (_busy || _traitReadInFlight)
            return;
        if (!RuntimeTagMemoryService.Current.IsConnected || (_traitsLive && !report))
            return;

        _traitReadInFlight = true;
        try
        {
            IReadOnlyList<PlayerModifierItem> items = await Task.Run(_modifiers.Read);
            BindTraits(items, live: true);
            if (report)
            {
                ShowStatus(
                    L.Format("cheat_globals.loaded_modifiers", items.Count),
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            _traitsLive = false;
            UpdateButtons();
            if (report)
                ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
        finally
        {
            _traitReadInFlight = false;
        }
    }

    private void ShowPlayerTeam(PlayerTeamState state)
    {
        _loadingTeam = true;
        _teamState = state;
        PlayerTeamOption selected = PlayerTeamService.Options.FirstOrDefault(
                option => option.Value == state.Selected.Value)
            ?? state.Selected;
        if (!PlayerTeamService.Options.Contains(selected))
        {
            PlayerTeamComboBox.ItemsSource =
                PlayerTeamService.Options.Append(selected).ToArray();
        }
        else
        {
            PlayerTeamComboBox.ItemsSource = PlayerTeamService.Options;
        }
        PlayerTeamComboBox.SelectedItem = selected;
        PlayerTeamDescriptionText.Text = selected.Description;
        _loadingTeam = false;
        UpdateSummary();
        UpdateButtons();
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try { await action(); }
        catch (Exception ex) { ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error); }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private async void OnEnableSuperPunch(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            float strength = SelectedSuperPunchStrength();
            SuperPunchResult result = await Task.Run(() => _superPunch.Enable(strength));
            ShowStatus(
                L.Format("player_tools.super_punch_enabled", result.Multiplier),
                InfoBarSeverity.Warning);
        });
    }

    private async void OnRestoreSuperPunch(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            await Task.Run(_superPunch.Restore);
            ShowStatus(L.Get("player_tools.normal_punch_restored"), InfoBarSeverity.Success);
        });
    }

    private async void OnWeaponInterruptionToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingActionTimingToggle ||
            _busy ||
            sender is not ToggleSwitch toggle)
        {
            return;
        }

        bool requested = toggle.IsOn;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            if (requested)
            {
                await Task.Run(_actionTiming.Enable);
                ShowStatus(
                    L.Get("player_tools.immediate_interruption_active"),
                    InfoBarSeverity.Warning);
            }
            else
            {
                await Task.Run(_actionTiming.Restore);
                ShowStatus(
                    L.Get("player_tools.authored_timing_restored"),
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            _updatingActionTimingToggle = true;
            toggle.IsOn = !requested;
            _updatingActionTimingToggle = false;
            ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private void SyncWeaponInterruptionToggle()
    {
        _updatingActionTimingToggle = true;
        ImmediateWeaponInterruptionToggle.IsOn = _actionTiming.IsActive;
        _updatingActionTimingToggle = false;
    }

    private float SelectedSuperPunchStrength()
    {
        if (SuperPunchStrengthBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag ||
            !float.TryParse(
                tag,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float strength))
        {
            return 50f;
        }

        return strength;
    }

    private async void OnRunOtherFeature(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OtherFeatureItem feature })
            return;

        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.HaloScript,
                feature.Script);
            if (result.Outcome is ScriptOutcome.Submitted or ScriptOutcome.Confirmed)
            {
                ShowStatus(L.Get(feature.SuccessKey), InfoBarSeverity.Success);
                return;
            }

            ShowStatus(UserFacingErrors.FromBridge(result.Message), InfoBarSeverity.Error);
        });
    }

    private void UpdateSummary()
    {
        CheatsCountText.Text = _cheatsLive
            ? $"{_items.Count(item => item.IsEnabled)} / {_items.Count}"
            : "";
        TraitsCountText.Text = _traitsLive
            ? $"{_modifierItems.Count(item => !item.SelectedOption.Label.Equals("Default", StringComparison.OrdinalIgnoreCase))} / {_modifierItems.Count}"
            : "";

        SummaryText.Text = _section switch
        {
            "allegiance" =>
                _teamState is null
                    ? L.Get("cheat_globals.allegiance_not_loaded")
                    : L.Format(
                        "cheat_globals.allegiance_label",
                        _teamState.Selected.Label),
            _ => BuildQuickSummary(),
        };
    }

    private string BuildQuickSummary()
    {
        string? cheats = _cheatsLive
            ? L.Format(
                "cheat_globals.cheats_active_summary",
                _items.Count(item => item.IsEnabled),
                _items.Count)
            : null;
        string? traits = _traitsLive
            ? L.Format(
                "cheat_globals.traits_non_default_summary",
                _modifierItems.Count(item =>
                    !item.SelectedOption.Label.Equals(
                        "Default",
                        StringComparison.OrdinalIgnoreCase)),
                _modifierItems.Count)
            : null;
        if (cheats is null)
            return traits ?? "";
        return traits is null ? cheats : $"{cheats} · {traits}";
    }

    private void UpdateButtons()
    {
        ScriptingBridgeStatus bridge = _cheats.BridgeStatus;
        bool ready = !_busy && bridge.IsRuntimeReady && !bridge.IsStale;
        bool memoryReady = !_busy && RuntimeTagMemoryService.Current.IsConnected;
        RefreshButton.IsEnabled = ready;
        DisableAllButton.IsEnabled =
            ready && _cheatsLive && _items.Any(item => item.IsEnabled);
        GlobalsList.IsEnabled = ready && _cheatsLive;
        RefreshModifiersButton.IsEnabled = memoryReady;
        RestoreModifiersButton.IsEnabled =
            memoryReady && _traitsLive && _modifiers.HasChanges;
        ModifierList.IsEnabled = memoryReady && _traitsLive;
        foreach (OtherFeatureItem feature in _otherFeatures)
            feature.IsEnabled = ready;
        SuperPunchStrengthBox.IsEnabled = memoryReady;
        EnableSuperPunchButton.IsEnabled = memoryReady;
        RestoreSuperPunchButton.IsEnabled = memoryReady && _superPunch.IsActive;
        ImmediateWeaponInterruptionToggle.IsEnabled = memoryReady;
        LoadPlayerTeamButton.IsEnabled = ready;
        PlayerTeamComboBox.IsEnabled = ready && _teamState is not null;
        RestorePlayerTeamButton.IsEnabled =
            ready && _teamState?.HasSnapshot == true;
        bool hasGlobalTeam =
            GlobalAllegianceTeamComboBox.SelectedItem is PlayerTeamOption;
        GlobalAllegianceTeamComboBox.IsEnabled = ready;
        GlobalAllegianceAllyButton.IsEnabled = ready && hasGlobalTeam;
        GlobalAllegianceBreakButton.IsEnabled = ready && hasGlobalTeam;
    }

    private void UpdateBridgeStatus()
    {
        BridgeStatusText.Text = _cheats.BridgeStatus.Summary;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
        => MainWindow.Instance?.Report(message, severity);
}
