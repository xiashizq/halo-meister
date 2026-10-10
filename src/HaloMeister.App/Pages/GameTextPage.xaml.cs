using System.Collections.ObjectModel;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HaloMeister.App.Pages;

public partial class GameTextPage : Page, IActivatablePage
{
    private readonly GameTextService _text;
    private readonly string _keyPrefix;
    private readonly ObservableCollection<GameTextFile> _languages = [];
    private List<GameTextEntry> _entries = [];
    private string? _language;
    private bool _scanned;
    private bool _suppressSelection;
    private bool _busy;
    private int _loadVersion;

    public GameTextPage()
        : this(GameTextService.Current, "game_text")
    {
    }

    protected GameTextPage(GameTextService text, string keyPrefix)
    {
        _text = text;
        _keyPrefix = keyPrefix;
        InitializeComponent();
        LanguageList.ItemsSource = _languages;
        EmptyTitle.Text = L.Get($"{_keyPrefix}.empty_title");
        EmptyHint.Text = L.Get($"{_keyPrefix}.empty_hint");
        CountText.Text = L.Get($"{_keyPrefix}.entries");
    }

    public void OnActivated()
    {
        if (_scanned)
            return;
        _scanned = true;
        _ = ScanAsync(refresh: false);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await ScanAsync(refresh: true);

    private async void OnLanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
            return;
        if (LanguageList.SelectedItem is not GameTextFile file)
            return;
        if (string.Equals(file.Language, _language, StringComparison.OrdinalIgnoreCase)
            && _entries.Count > 0)
            return;
        _language = file.Language;
        await LoadLanguageAsync(file);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;
        if ((button.Tag as GameTextRow ?? button.DataContext as GameTextRow) is not GameTextRow row)
            return;
        var package = new DataPackage();
        package.SetText(row.Text);
        Clipboard.SetContent(package);
    }

    private async Task ScanAsync(bool refresh)
    {
        if (_busy && !refresh)
            return;
        SetBusy(true);
        try
        {
            IReadOnlyList<GameTextFile> files = refresh
                ? await _text.RefreshAsync()
                : await _text.ListAsync();
            string? previous = _language;
            _suppressSelection = true;
            _languages.Clear();
            foreach (GameTextFile file in files)
                _languages.Add(file);
            GameTextFile? match = _languages.FirstOrDefault(file =>
                string.Equals(file.Language, previous, StringComparison.OrdinalIgnoreCase))
                ?? _languages.FirstOrDefault();
            LanguageList.SelectedItem = match;
            _suppressSelection = false;
            if (match is null)
            {
                _language = null;
                _entries = [];
                EntryList.ItemsSource = null;
                ShowEmpty(
                    L.Get($"{_keyPrefix}.empty_title"),
                    L.Get($"{_keyPrefix}.empty_hint"));
                return;
            }
            _language = match.Language;
            await LoadLanguageAsync(match);
        }
        catch (Exception ex)
        {
            _languages.Clear();
            _entries = [];
            EntryList.ItemsSource = null;
            ShowEmpty(L.Get($"{_keyPrefix}.empty_title"), UserFacingErrors.Format(ex));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadLanguageAsync(GameTextFile file)
    {
        int version = ++_loadVersion;
        SetBusy(true);
        try
        {
            IReadOnlyList<GameTextEntry> entries = await _text.ReadAsync(file.Path);
            if (version != _loadVersion)
                return;
            _entries = entries.ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            if (version != _loadVersion)
                return;
            _entries = [];
            EntryList.ItemsSource = null;
            ShowEmpty(L.Get($"{_keyPrefix}.empty_title"), UserFacingErrors.Format(ex));
        }
        finally
        {
            if (version == _loadVersion)
                SetBusy(false);
        }
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        IEnumerable<GameTextEntry> source = _entries;
        if (query.Length > 0)
        {
            source = _entries.Where(entry =>
                entry.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Namespace.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var matched = source.Select(entry => new GameTextRow(entry)).ToList();
        EntryList.ItemsSource = matched;

        bool empty = matched.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EntryList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ColumnHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = L.Format(
            $"{_keyPrefix}.count",
            matched.Count.ToString("N0"),
            _language ?? "");
        if (!empty)
            return;
        if (_entries.Count == 0)
        {
            EmptyTitle.Text = L.Get($"{_keyPrefix}.empty_title");
            EmptyHint.Text = L.Get($"{_keyPrefix}.empty_language");
        }
        else
        {
            EmptyTitle.Text = L.Get($"{_keyPrefix}.no_match");
            EmptyHint.Text = L.Get($"{_keyPrefix}.no_match_hint");
        }
    }

    private void ShowEmpty(string title, string hint)
    {
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
        EmptyState.Visibility = Visibility.Visible;
        EntryList.Visibility = Visibility.Collapsed;
        ColumnHeader.Visibility = Visibility.Collapsed;
        CountText.Text = L.Get($"{_keyPrefix}.entries");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        RefreshButton.IsEnabled = !busy;
        LanguageList.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
    }
}

public sealed class GameTextRow
{
    public GameTextRow(GameTextEntry entry)
    {
        Namespace = entry.Namespace;
        Key = entry.Key;
        Text = entry.Text;
    }

    public string Namespace { get; }
    public string Key { get; }
    public string Text { get; }
    public Visibility NamespaceVisibility =>
        string.IsNullOrEmpty(Namespace) ? Visibility.Collapsed : Visibility.Visible;
}
