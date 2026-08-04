using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class AllegianceDemoPage : Page
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly AllegianceDemoService _demo = new();
    private IReadOnlyList<EnemySpawnChoice> _characters = [];
    private EnemySpawnChoice? _selectedCharacter;
    private int? _lastActorDatum;
    private bool _busy;

    public AllegianceDemoPage()
    {
        InitializeComponent();
        TeamComboBox.ItemsSource = AllegianceDemoService.TeamOptions;
        TeamComboBox.SelectedItem = AllegianceDemoService.TeamOptions
            .FirstOrDefault(option => option.Value == 1);
        _game.ConnectionChanged += OnConnectionChanged;
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
            ShowStatus(
                L.Format("allegiance_demo.scanned", _characters.Count),
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
        EnemySpawnChoice character = _selectedCharacter;
        SpawnVariantChoice variant = character.Variants.FirstOrDefault()
            ?? throw new InvalidOperationException(L.Get("spawner.select_character_variant"));
        await RunBusy(async () =>
        {
            AllegianceDemoSpawnResult spawn = await _demo.SpawnAsync(character, variant);
            _lastActorDatum = spawn.ActorDatum;
            LastActorText.Text = _lastActorDatum is int actor
                ? L.Format("allegiance_demo.last_actor", $"0x{actor:X8}")
                : L.Format("allegiance_demo.last_actor_unknown", spawn.SpawnResult.Message);
            if (spawn.SpawnResult.Outcome == ScriptOutcome.Failed)
            {
                ShowStatus(spawn.SpawnResult.Message, InfoBarSeverity.Error);
                return;
            }
            ShowStatus(
                L.Format("allegiance_demo.spawned", character.DisplayName, spawn.SpawnResult.Message),
                InfoBarSeverity.Success);
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
        ScanButton.IsEnabled = !_busy && connected;
        SpawnButton.IsEnabled =
            !_busy && connected && ready && _selectedCharacter is not null;
        bool hasTeam = TeamComboBox.SelectedItem is PlayerTeamOption;
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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _game.ConnectionChanged -= OnConnectionChanged;
        Unloaded -= OnUnloaded;
    }
}
