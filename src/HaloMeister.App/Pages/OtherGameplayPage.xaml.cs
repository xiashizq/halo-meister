using System.ComponentModel;
using System.Runtime.CompilerServices;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class OtherGameplayPage : Page, IActivatablePage
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

    private static readonly OtherFeatureDefinition[] FeatureDefinitions =
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

    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<OtherFeatureItem> _features = [];
    private bool _busy;

    public OtherGameplayPage()
    {
        InitializeComponent();
        foreach (OtherFeatureDefinition definition in FeatureDefinitions)
        {
            _features.Add(new OtherFeatureItem
            {
                Title = L.Get(definition.TitleKey),
                Description = L.Get(definition.DescriptionKey),
                ActionLabel = L.Get(definition.ActionKey),
                Script = definition.Script,
                SuccessKey = definition.SuccessKey,
            });
        }

        FeaturesList.ItemsSource = _features;
        _statusTimer.Tick += OnStatusTimer;
    }

    public void OnActivated()
    {
        UpdateBridgeStatus();
        _statusTimer.Start();
    }

    public void OnDeactivated() => _statusTimer.Stop();

    private void OnStatusTimer(object? sender, object e) => UpdateBridgeStatus();

    private async void OnRunFeature(object sender, RoutedEventArgs e)
    {
        if (_busy ||
            sender is not FrameworkElement { Tag: OtherFeatureItem feature })
            return;

        _busy = true;
        BusyRing.IsActive = true;
        UpdateFeatureEnabled();
        try
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.HaloScript,
                feature.Script);
            string message =
                result.Outcome == ScriptOutcome.Submitted
                    ? L.Get(feature.SuccessKey)
                    : result.Message;
            ShowStatus(message, Severity(result.Outcome));
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
        }
    }

    private void UpdateBridgeStatus()
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        BridgeStatusText.Text = status.IsRuntimeReady && !status.IsStale
            ? L.Format(
                "other_gameplay.ready_bridge",
                status.RunningVersion,
                status.LastHeartbeat?.ToString("HH:mm:ss") ?? L.Get("common.unknown"))
            : status.Summary;
        UpdateFeatureEnabled(status);
    }

    private void UpdateFeatureEnabled(ScriptingBridgeStatus? status = null)
    {
        status ??= _bridge.GetStatus();
        bool enabled = !_busy && status.IsRuntimeReady && !status.IsStale;
        foreach (OtherFeatureItem feature in _features)
            feature.IsEnabled = enabled;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private static InfoBarSeverity Severity(ScriptOutcome outcome) => outcome switch
    {
        ScriptOutcome.Confirmed => InfoBarSeverity.Success,
        ScriptOutcome.Submitted => InfoBarSeverity.Success,
        _ => InfoBarSeverity.Error,
    };
}
