using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class AllegianceDemoPage : Page
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly AllegianceDemoService _demo = new();
    private readonly FullPalettesOverlayService _builtinMod = new();
    private readonly DispatcherTimer _statusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };
    private IReadOnlyList<EnemySpawnChoice> _characters = [];
    private EnemySpawnChoice? _selectedCharacter;
    private int? _lastActorDatum;
    private bool _busy;

    public AllegianceDemoPage()
    {
        InitializeComponent();
        IReadOnlyList<PlayerTeamOption> stances =
            AllegianceDemoService.CreateTeamOptions();
        TeamComboBox.ItemsSource = stances;
        TeamComboBox.SelectedItem = stances.FirstOrDefault(
            option => option.Value == AllegianceDemoService.FriendlyTeam);
        _game.ConnectionChanged += OnConnectionChanged;
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();
        Unloaded += OnUnloaded;
        UpdateControls();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            SpawnerCatalog catalog = await Task.Run(_demo.Connect);
            _characters = catalog.Characters;
            CharacterComboBox.ItemsSource = _characters;
            if (_characters.Count > 0)
                CharacterComboBox.SelectedIndex = 0;
            SpawnScaffoldInventory inventory = await Task.Run(_demo.ProbeScaffolds);
            string probe = L.Format(
                "allegiance_demo.scaffold_probe",
                inventory.AllyScaffoldCount,
                inventory.IdleAllyCount,
                inventory.HostileScaffoldCount,
                inventory.DedicatedAllyCount,
                inventory.DedicatedHostileCount,
                _demo.ScaffoldDiagnosisLogPath);
            if (inventory.NeedsDedicatedAlly)
            {
                ShowStatus(
                    L.Format("allegiance_demo.scanned", _characters.Count) +
                    " " +
                    probe +
                    " " +
                    L.Get("allegiance_demo.needs_dedicated_ally"),
                    InfoBarSeverity.Warning);
                return;
            }
            ShowStatus(
                L.Format("allegiance_demo.scanned", _characters.Count) + " " + probe,
                InfoBarSeverity.Success);
        });
    }

    private void OnCharacterChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedCharacter = CharacterComboBox.SelectedItem as EnemySpawnChoice;
        UpdateControls();
    }

    private async void OnSpawn(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacter is null) return;
        if (TeamComboBox.SelectedItem is not PlayerTeamOption team)
            return;
        if (!_builtinMod.IsInstalled())
        {
            ShowStatus(
                L.Get("allegiance_demo.mod_required_body"),
                InfoBarSeverity.Warning);
            return;
        }
        EnemySpawnChoice character = _selectedCharacter;
        SpawnVariantChoice variant = character.Variants.FirstOrDefault()
            ?? throw new InvalidOperationException(L.Get("spawner.select_character_variant"));
        await RunBusy(async () =>
        {
            // Prefer a matching scaffold and birth into the selected campaign team.
            AllegianceDemoSpawnResult spawn = await _demo.SpawnAsync(
                character,
                variant,
                team.Value);
            _lastActorDatum = spawn.ActorDatum;
            string scaffoldHint = spawn.ScaffoldDiagnosis?.Summary ?? "";
            LastActorText.Text = _lastActorDatum is int actor
                ? L.Format(
                    "allegiance_demo.last_actor",
                    string.IsNullOrWhiteSpace(scaffoldHint)
                        ? $"0x{actor:X8} · {team.Label}"
                        : $"0x{actor:X8} · {team.Label} · {scaffoldHint}")
                : L.Format("allegiance_demo.last_actor_unknown", spawn.SpawnResult.Message);
            if (spawn.SpawnResult.Outcome == ScriptOutcome.Failed)
            {
                ShowStatus(spawn.SpawnResult.Message, InfoBarSeverity.Error);
                return;
            }
            bool hostileFallback =
                spawn.ScaffoldDiagnosis?.UsedHostileFallback == true;
            ShowStatus(
                L.Format(
                    "allegiance_demo.spawned",
                    $"{character.DisplayName} ({team.Label})",
                    spawn.SpawnResult.Message),
                hostileFallback ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        });
    }

    private async void OnApplyTeam(object sender, RoutedEventArgs e)
    {
        if (TeamComboBox.SelectedItem is not PlayerTeamOption team)
            return;
        await RunBusy(async () =>
        {
            ObjectTeamResult result = await _demo.ApplyObjectTeamAsync(
                team.Value,
                _lastActorDatum);
            LastActorText.Text = L.Format(
                "allegiance_demo.applied_team",
                $"0x{result.UnitDatum:X8}",
                $"0x{result.ActorDatum:X8}",
                team.Label);
            ShowStatus(
                L.Format("allegiance_demo.apply_ok", team.Label),
                InfoBarSeverity.Success);
        });
    }

    private async void OnAlly(object sender, RoutedEventArgs e)
    {
        if (TeamComboBox.SelectedItem is not PlayerTeamOption team)
            return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _demo.SubmitAllegianceAsync(
                team.Value,
                breakAllegiance: false);
            ShowScriptResult(
                result,
                L.Format(
                    "allegiance_demo.hs_ally_submitted",
                    AllegianceDemoService.HaloScriptTeamName(team.Value)));
        });
    }

    private async void OnBreak(object sender, RoutedEventArgs e)
    {
        if (TeamComboBox.SelectedItem is not PlayerTeamOption team)
            return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _demo.SubmitAllegianceAsync(
                team.Value,
                breakAllegiance: true);
            ShowScriptResult(
                result,
                L.Format(
                    "allegiance_demo.hs_break_submitted",
                    AllegianceDemoService.HaloScriptTeamName(team.Value)));
        });
    }

    private void ShowScriptResult(ScriptExecutionResult result, string successMessage)
    {
        if (result.Outcome == ScriptOutcome.Failed)
        {
            ShowStatus(result.Message, InfoBarSeverity.Error);
            return;
        }
        ShowStatus(
            $"{successMessage} {result.Message}",
            result.Outcome == ScriptOutcome.Confirmed
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Informational);
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateControls();
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
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        BridgeStatusText.Text = _demo.BridgeStatus.Summary;
        BusyRing.IsActive = _busy;
        bool connected = _game.IsConnected;
        bool ready = _demo.BridgeStatus.IsRuntimeReady;
        bool modReady = _builtinMod.IsInstalled();
        ModRequiredBanner.IsOpen = !modReady;
        ScanButton.IsEnabled = !_busy && connected;
        bool hasTeam = TeamComboBox.SelectedItem is PlayerTeamOption;
        // Dedicated friend/foe spawn needs the complete MMYJ_FULL_VEHI_WAP_P triplet.
        SpawnButton.IsEnabled =
            !_busy && connected && ready && modReady &&
            hasTeam && _selectedCharacter is not null;
        ApplyTeamButton.IsEnabled = !_busy && ready && hasTeam;
        AllyButton.IsEnabled = !_busy && ready && hasTeam;
        BreakButton.IsEnabled = !_busy && ready && hasTeam;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private void OnConnectionChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateControls);

    private void OnStatusTick(object? sender, object e) => UpdateControls();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTick;
        _game.ConnectionChanged -= OnConnectionChanged;
        Unloaded -= OnUnloaded;
    }
}
