using System.Diagnostics;
using System.Text;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace HaloMeister.App.Pages;

public sealed partial class ScriptingPage : Page, IActivatablePage
{
    private sealed record ScriptExample(string NameKey, string Code)
    {
        public string Name => L.Get(NameKey);
    }

    private const string DefaultLuaScript =
        "print(\"[HaloMeister] Lua is running\")\nreturn \"ok\"";
    private const string DefaultHaloScript = "(fade_out 0 0 0 0)";
    private static readonly IReadOnlyList<ScriptExample> HaloScriptExamples =
    [
        new("scripting.example_instant_fade_out", "(fade_out 0 0 0 0)"),
        new("scripting.example_instant_fade_in", "(fade_in 0 0 0 0)"),
        new("scripting.example_kill_player", "unit_kill (player0)"),
    ];
    private static readonly IReadOnlyList<ScriptExample> LuaExamples =
    [
        new("scripting.example_hello_lua", DefaultLuaScript),
        new(
            "scripting.example_active_player",
            "local get_player = UEHelpers.GetPlayerPawn or UEHelpers.GetPlayer\n" +
            "assert(get_player, \"UEHelpers has no player accessor\")\n" +
            "local player = get_player()\n" +
            "assert(player and player:IsValid(), \"Load a campaign mission first\")\n" +
            "return player:GetFullName()"),
        new(
            "scripting.example_player_position",
            "local get_player = UEHelpers.GetPlayerPawn or UEHelpers.GetPlayer\n" +
            "assert(get_player, \"UEHelpers has no player accessor\")\n" +
            "local player = get_player()\n" +
            "assert(player and player:IsValid(), \"Load a campaign mission first\")\n" +
            "local p = player:K2_GetActorLocation()\n" +
            "return string.format(\"X %.2f  Y %.2f  Z %.2f\", p.X, p.Y, p.Z)"),
    ];
    private static string _luaSessionDraft = DefaultLuaScript;
    private static string _haloScriptSessionDraft = DefaultHaloScript;
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _executionCancellation;
    private ScriptLanguage _selectedLanguage = ScriptLanguage.HaloScript;
    private bool _changingLanguage;
    private bool _suppressAutocomplete;
    private int _autocompleteTokenStart;
    private int _autocompleteTokenLength;
    private bool _busy;

    public ScriptingPage()
    {
        InitializeComponent();
        ScriptEditor.Text = _haloScriptSessionDraft;
        SetExamples(HaloScriptExamples);
        LoadHaloScriptCatalog();
        UpdateScriptStats();
        _statusTimer.Tick += OnStatusTimer;
    }

    public void OnActivated()
    {
        UpdateBridgeStatus();
        _statusTimer.Start();
    }

    public void OnDeactivated()
    {
        _statusTimer.Stop();
        _executionCancellation?.Cancel();
    }

    private void OnStatusTimer(object? sender, object e) => UpdateBridgeStatus();
    private void OnRefreshStatus(object sender, RoutedEventArgs e) => UpdateBridgeStatus();

    private void OnOpenBridgeFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_bridge.BridgeRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_bridge.BridgeRoot}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnRunScript(object sender, RoutedEventArgs e)
        => await RunScript(_selectedLanguage, ScriptEditor.Text);

    private void OnCancel(object sender, RoutedEventArgs e)
        => _executionCancellation?.Cancel();

    private void OnScriptTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_changingLanguage)
        {
            if (_selectedLanguage == ScriptLanguage.Lua)
                _luaSessionDraft = ScriptEditor.Text;
            else
                _haloScriptSessionDraft = ScriptEditor.Text;
        }
        UpdateScriptStats();
        if (!_changingLanguage && !_suppressAutocomplete)
            UpdateAutocomplete();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguagePicker.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        if (ScriptEditor is null || RunScriptButton is null || ScriptRuntimeText is null)
            return;

        _selectedLanguage = tag == "haloscript"
            ? ScriptLanguage.HaloScript
            : ScriptLanguage.Lua;
        _changingLanguage = true;
        ScriptEditor.Text = _selectedLanguage == ScriptLanguage.Lua
            ? _luaSessionDraft
            : _haloScriptSessionDraft;
        _changingLanguage = false;
        RunScriptButton.Content = _selectedLanguage == ScriptLanguage.Lua
            ? L.Get("scripting.run_lua_button")
            : L.Get("scripting.run_haloscript_button");
        ScriptRuntimeText.Text = _selectedLanguage == ScriptLanguage.Lua
            ? L.Get("scripting.runtime_lua")
            : L.Get("scripting.use_hs_command_style_calls_or_parenthesiz_34740c");
        SetExamples(_selectedLanguage == ScriptLanguage.Lua
            ? LuaExamples
            : HaloScriptExamples);
        HideAutocomplete();
        UpdateScriptStats();
    }

    private void OnLoadExample(object sender, RoutedEventArgs e)
    {
        if (ExamplePicker.SelectedItem is not ScriptExample example)
            return;
        ScriptEditor.Text = example.Code;
        ScriptSourceText.Text = L.Format("scripting.starter_source", example.Name);
    }

    private async void OnOpenScript(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
            picker.FileTypeFilter.Add(".lua");
            picker.FileTypeFilter.Add(".hsc");
            picker.FileTypeFilter.Add(".hs");
            picker.FileTypeFilter.Add(".txt");
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            if (file.FileType.Equals(".lua", StringComparison.OrdinalIgnoreCase))
                LanguagePicker.SelectedIndex = 1;
            else if (file.FileType.Equals(".hsc", StringComparison.OrdinalIgnoreCase) ||
                     file.FileType.Equals(".hs", StringComparison.OrdinalIgnoreCase))
                LanguagePicker.SelectedIndex = 0;
            ScriptEditor.Text = await FileIO.ReadTextAsync(file);
            ScriptSourceText.Text = file.Path;
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnSaveScript(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "halo-meister-script",
            };
            if (_selectedLanguage == ScriptLanguage.HaloScript)
            {
                picker.FileTypeChoices.Add(L.Get("scripting.file_type_haloscript"), new List<string> { ".hsc" });
                picker.FileTypeChoices.Add(L.Get("scripting.file_type_lua"), new List<string> { ".lua" });
            }
            else
            {
                picker.FileTypeChoices.Add(L.Get("scripting.file_type_lua"), new List<string> { ".lua" });
                picker.FileTypeChoices.Add(L.Get("scripting.file_type_haloscript"), new List<string> { ".hsc" });
            }
            picker.FileTypeChoices.Add(L.Get("scripting.file_type_text"), new List<string> { ".txt" });
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await FileIO.WriteTextAsync(file, ScriptEditor.Text);
            ScriptSourceText.Text = file.Path;
            ShowStatus(L.Format("scripting.saved_file", file.Path), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(OutputText.Text);
        Clipboard.SetContent(package);
    }

    private void OnClearOutput(object sender, RoutedEventArgs e)
    {
        OutputText.Text = L.Get("scripting.no_scripts_have_been_run_in_this_session");
        OutputSummaryText.Text = L.Get("scripting.no_scripts_run_in_this_session");
    }

    private async Task RunScript(ScriptLanguage language, string code)
    {
        if (_busy)
            return;

        _busy = true;
        BusyRing.IsActive = true;
        SetButtonsEnabled(false);
        _executionCancellation = new CancellationTokenSource();
        try
        {
            AppendOutput(L.Format("scripting.request_submitted", LanguageName(language)));
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                language,
                code,
                cancellationToken: _executionCancellation.Token);
            AppendOutput(
                $"[{DateTime.Now:HH:mm:ss}] {Label(result.Outcome)} " +
                $"({result.Elapsed.TotalMilliseconds:0} ms)\n{result.Message}");
            OutputSummaryText.Text = L.Format(
                "scripting.language_outcome",
                LanguageName(language),
                Label(result.Outcome).ToLowerInvariant(),
                result.Elapsed.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            string statusMessage =
                language == ScriptLanguage.HaloScript &&
                result.Outcome == ScriptOutcome.Submitted
                    ? L.Get("scripting.haloscript_submitted")
                    : result.Message;
            ShowStatus(statusMessage, Severity(result.Outcome));
        }
        catch (OperationCanceledException)
        {
            AppendOutput(L.Get("scripting.request_cancelled"));
        }
        catch (Exception ex)
        {
            AppendOutput($"[{DateTime.Now:HH:mm:ss}] {L.Get("scripting.outcome_error")}\n{ex.Message}");
            OutputSummaryText.Text = L.Format("scripting.language_error", LanguageName(language));
            OutputExpander.IsExpanded = true;
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _executionCancellation?.Dispose();
            _executionCancellation = null;
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
        }
    }

    private void UpdateBridgeStatus()
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        BridgeStatusText.Text = status.IsRuntimeReady && !status.IsStale
            ? L.Format("scripting.ready_bridge", status.RunningVersion, status.LastHeartbeat?.ToString("HH:mm:ss") ?? L.Get("common.unknown"))
            : status.Summary;
        BridgePathText.Text = status.InstalledMainPath ?? L.Format("scripting.mailbox_path", _bridge.BridgeRoot);
        string colorKey = status.IsRuntimeReady && !status.IsStale
            ? "SystemFillColorSuccessBrush"
            : status.IsRuntimeReady
                ? "SystemFillColorCautionBrush"
                : "SystemFillColorCriticalBrush";
        BridgeStatusIcon.Foreground =
            (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[colorKey];
        SetButtonsEnabled(status.IsRuntimeReady);
    }

    private void SetButtonsEnabled(bool runtimeReady)
    {
        bool enabled = !_busy && runtimeReady;
        RunScriptButton.IsEnabled = enabled;
        CancelButton.IsEnabled = _busy;
        OpenScriptButton.IsEnabled = !_busy;
        SaveScriptButton.IsEnabled = !_busy;
    }

    private void UpdateScriptStats()
    {
        int bytes = Encoding.UTF8.GetByteCount(ScriptEditor.Text);
        ScriptStatsText.Text = L.Format(
            "scripting.characters_kib",
            ScriptEditor.Text.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
            (bytes / 1024d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        ScriptStatsText.Foreground = bytes > 64 * 1024
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            : null;
    }

    private void AppendOutput(string text)
    {
        if (OutputText.Text == L.Get("scripting.no_scripts_have_been_run_in_this_session"))
            OutputText.Text = text;
        else
            OutputText.Text += $"\n\n{text}";
        OutputText.Select(OutputText.Text.Length, 0);
    }

    private void SetExamples(IReadOnlyList<ScriptExample> examples)
    {
        ExamplePicker.ItemsSource = examples;
        ExamplePicker.SelectedIndex = examples.Count > 0 ? 0 : -1;
    }

    private void LoadHaloScriptCatalog()
    {
        IReadOnlyList<HaloScriptReference> catalog = HaloScriptCatalog.Load();
        CatalogCountText.Text = catalog.Count == 0
            ? L.Get("scripting.catalog_unavailable")
            : L.Format("scripting.catalog_count", catalog.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        ReferenceList.ItemsSource = HaloScriptCatalog.Search(null);
        ReferenceList.SelectedIndex = ReferenceList.Items.Count > 0 ? 0 : -1;
    }

    private void OnCommandSearchTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        IReadOnlyList<HaloScriptReference> results =
            HaloScriptCatalog.Search(sender.Text);
        sender.ItemsSource = results.Take(12).ToArray();
        ReferenceList.ItemsSource = results;
        ReferenceList.SelectedIndex = ReferenceList.Items.Count > 0 ? 0 : -1;
    }

    private void OnCommandSuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is HaloScriptReference item)
            sender.Text = item.Name;
    }

    private void OnCommandQuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is HaloScriptReference chosen)
        {
            ReferenceList.SelectedItem = chosen;
            InsertReference(chosen);
            return;
        }

        HaloScriptReference? exact = HaloScriptCatalog.Search(args.QueryText)
            .FirstOrDefault(item =>
                item.Name.Equals(args.QueryText, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            ReferenceList.SelectedItem = exact;
            InsertReference(exact);
        }
    }

    private void OnReferenceItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HaloScriptReference item)
            ReferenceList.SelectedItem = item;
    }

    private void OnInsertReference(object sender, RoutedEventArgs e)
    {
        if (ReferenceList.SelectedItem is HaloScriptReference item)
            InsertReference(item);
    }

    private void OnScriptEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_changingLanguage && !_suppressAutocomplete)
            UpdateAutocomplete();
    }

    private void OnScriptEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool controlDown =
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) &
             CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        if (controlDown && e.Key == VirtualKey.Space)
        {
            UpdateAutocomplete(force: true);
            e.Handled = true;
            return;
        }

        if (AutocompletePanel.Visibility != Visibility.Visible)
            return;

        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveAutocompleteSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveAutocompleteSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Tab:
            case VirtualKey.Enter:
                AcceptAutocomplete(AutocompleteList.SelectedItem as HaloScriptReference);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                HideAutocomplete();
                e.Handled = true;
                break;
        }
    }

    private void OnAutocompleteItemClick(object sender, ItemClickEventArgs e)
        => AcceptAutocomplete(e.ClickedItem as HaloScriptReference);

    private void UpdateAutocomplete(bool force = false)
    {
        if (_selectedLanguage != ScriptLanguage.HaloScript || ScriptEditor is null)
        {
            HideAutocomplete();
            return;
        }

        int caret = Math.Clamp(ScriptEditor.SelectionStart, 0, ScriptEditor.Text.Length);
        int start = caret;
        while (start > 0 && IsHaloScriptIdentifierCharacter(ScriptEditor.Text[start - 1]))
            start--;

        string prefix = ScriptEditor.Text[start..caret];
        if (!force && prefix.Length < 2)
        {
            HideAutocomplete();
            return;
        }

        IReadOnlyList<HaloScriptReference> results =
            HaloScriptCatalog.Search(prefix.Length == 0 ? null : prefix, 10);
        if (results.Count == 0)
        {
            HideAutocomplete();
            return;
        }

        _autocompleteTokenStart = start;
        _autocompleteTokenLength = caret - start;
        AutocompleteList.ItemsSource = results;
        AutocompleteList.SelectedIndex = 0;
        AutocompleteHintText.Text = prefix.Length == 0
            ? L.Get("scripting.autocomplete_common")
            : L.Format("scripting.autocomplete_prefix", prefix);
        AutocompletePanel.Visibility = Visibility.Visible;
    }

    private void MoveAutocompleteSelection(int direction)
    {
        int count = AutocompleteList.Items.Count;
        if (count == 0)
            return;
        int current = Math.Max(0, AutocompleteList.SelectedIndex);
        AutocompleteList.SelectedIndex = (current + direction + count) % count;
        AutocompleteList.ScrollIntoView(AutocompleteList.SelectedItem);
    }

    private void AcceptAutocomplete(HaloScriptReference? item)
    {
        if (item is null)
            return;

        string current = ScriptEditor.Text;
        if (_autocompleteTokenStart < 0 ||
            _autocompleteTokenStart + _autocompleteTokenLength > current.Length)
            return;

        _suppressAutocomplete = true;
        ScriptEditor.Text = current
            .Remove(_autocompleteTokenStart, _autocompleteTokenLength)
            .Insert(_autocompleteTokenStart, item.Name);
        ScriptEditor.SelectionStart = _autocompleteTokenStart + item.Name.Length;
        ScriptEditor.SelectionLength = 0;
        _suppressAutocomplete = false;
        HideAutocomplete();
        ScriptEditor.Focus(FocusState.Programmatic);
        ScriptSourceText.Text = L.Format("scripting.autocomplete_source", item.Name);
    }

    private void HideAutocomplete()
    {
        if (AutocompletePanel is not null)
            AutocompletePanel.Visibility = Visibility.Collapsed;
    }

    private static bool IsHaloScriptIdentifierCharacter(char value)
        => char.IsLetterOrDigit(value) || value == '_';

    private void OnOpenHaloScriptGuide(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.NavigateTo("help");

    private void OnOpenFullReference(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "HaloScript",
                "hs_doc.txt");
            if (!File.Exists(path))
                throw new FileNotFoundException(L.Get("scripting.reference_unavailable"), path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void InsertReference(HaloScriptReference item)
    {
        if (_selectedLanguage != ScriptLanguage.HaloScript)
            LanguagePicker.SelectedIndex = 0;

        string insertion = HaloScriptCatalog.CreateInsertion(item);
        int start = ScriptEditor.SelectionStart;
        int length = ScriptEditor.SelectionLength;
        string current = ScriptEditor.Text;
        ScriptEditor.Text = current.Remove(start, length).Insert(start, insertion);
        ScriptEditor.SelectionStart = start + insertion.Length - (item.IsGlobal ? 0 : 1);
        ScriptEditor.SelectionLength = 0;
        ScriptEditor.Focus(FocusState.Programmatic);
        ScriptSourceText.Text = L.Format("scripting.catalog_source", item.Name);
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        PageStatus.Title = severity switch
        {
            InfoBarSeverity.Error => L.Get("scripting.scripting_failed"),
            InfoBarSeverity.Warning => L.Get("scripting.submitted_not_verified"),
            InfoBarSeverity.Success => L.Get("scripting.scripting_title"),
            _ => L.Get("scripting.runtime_scripting"),
        };
        PageStatus.Message = message;
        PageStatus.Severity = severity;
        PageStatus.IsOpen = true;
    }

    private static string Label(ScriptOutcome outcome) => outcome switch
    {
        ScriptOutcome.Confirmed => L.Get("scripting.outcome_confirmed"),
        ScriptOutcome.Submitted => L.Get("scripting.outcome_submitted"),
        _ => L.Get("scripting.outcome_error"),
    };

    private static string LanguageName(ScriptLanguage language)
        => language == ScriptLanguage.HaloScript ? "HaloScript" : "Lua";

    private static InfoBarSeverity Severity(ScriptOutcome outcome) => outcome switch
    {
        ScriptOutcome.Confirmed => InfoBarSeverity.Success,
        ScriptOutcome.Submitted => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Error,
    };
}
