using System.Collections.ObjectModel;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class CharacterModelsPage : Page, IActivatablePage
{
    private readonly CharacterModelLibraryService _models = CharacterModelLibraryService.Current;
    private readonly ObservableCollection<CharacterModelCategory> _categories = [];
    private readonly ObservableCollection<CharacterModelFile> _modelsInFolder = [];
    private List<CharacterModelFile> _all = [];
    private CharacterModelFile? _current;
    private string? _folder;
    private bool _scanned;
    private bool _suppressSelection;

    public CharacterModelsPage()
    {
        InitializeComponent();
        CategoryList.ItemsSource = _categories;
        ModelBox.ItemsSource = _modelsInFolder;
    }

    public void OnActivated()
    {
        if (_scanned)
            return;
        _scanned = true;
        _ = ScanAsync(refresh: false);
    }

    public void OnDeactivated() => Preview.Suspend();

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await ScanAsync(refresh: true);

    private void OnCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
            return;
        string? folder = (CategoryList.SelectedItem as CharacterModelCategory)?.Folder;
        if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
            return;
        _folder = folder;
        ShowFolder(folder);
    }

    private async void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ModelBox.SelectedItem is not CharacterModelFile file)
            return;
        if (string.Equals(file.Id, _current?.Id, StringComparison.OrdinalIgnoreCase))
            return;
        _current = file;
        await Preview.ShowAsync(file);
    }

    private async Task ScanAsync(bool refresh)
    {
        SetBusy(true);
        try
        {
            _all = (refresh ? await _models.RefreshAsync() : await _models.ListAsync()).ToList();
            RebuildCategories();
            ShowFolder(_folder);
        }
        catch (Exception ex)
        {
            _all = [];
            _categories.Clear();
            _modelsInFolder.Clear();
            _current = null;
            EmptyTitle.Text = L.Get("character_model.empty_title");
            EmptyHint.Text = ex.Message;
            EmptyState.Visibility = Visibility.Visible;
            Preview.Visibility = Visibility.Collapsed;
            ModelBox.Visibility = Visibility.Collapsed;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RebuildCategories()
    {
        string? previous = _folder;
        var present = new HashSet<string>(
            _all.Select(file => file.Folder),
            StringComparer.OrdinalIgnoreCase);
        var folders = _all
            .Select(file => file.Folder)
            .Where(folder => !IsChildFolder(folder, present))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(folder => new CharacterModelCategory(
                string.IsNullOrEmpty(folder) ? "characters" : folder,
                folder))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _suppressSelection = true;
        _categories.Clear();
        foreach (CharacterModelCategory folder in folders)
            _categories.Add(folder);
        CharacterModelCategory? match = folders.FirstOrDefault(item =>
            string.Equals(item.Folder, previous, StringComparison.OrdinalIgnoreCase));
        _folder = match?.Folder ?? folders.FirstOrDefault()?.Folder;
        CategoryList.SelectedItem = _categories.FirstOrDefault(item =>
            string.Equals(item.Folder, _folder, StringComparison.OrdinalIgnoreCase));
        _suppressSelection = false;
    }

    private void ShowFolder(string? folder)
    {
        var files = _all
            .Where(file => string.Equals(file.Folder, folder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => KindRank(file.Kind))
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CharacterModelFile? model = files.FirstOrDefault(file => file.Kind == "model");
        CharacterModelFile? keep = files.FirstOrDefault(file =>
            string.Equals(file.Id, _current?.Id, StringComparison.OrdinalIgnoreCase))
            ?? model
            ?? files.FirstOrDefault();
        _suppressSelection = true;
        _modelsInFolder.Clear();
        foreach (CharacterModelFile file in files)
            _modelsInFolder.Add(file);
        ModelBox.SelectedItem = keep;
        _suppressSelection = false;

        bool empty = files.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ModelBox.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty)
        {
            _current = null;
            EmptyTitle.Text = _all.Count == 0
                ? L.Get("character_model.empty_title")
                : L.Get("character_model.select_folder");
            EmptyHint.Text = _all.Count == 0
                ? L.Get("character_model.empty_hint")
                : L.Get("character_model.select_folder_hint");
            return;
        }
        if (keep is not null && !string.Equals(keep.Id, _current?.Id, StringComparison.OrdinalIgnoreCase))
        {
            _current = keep;
            _ = Preview.ShowAsync(keep);
        }
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        RefreshButton.IsEnabled = !busy;
        ModelBox.IsEnabled = !busy;
    }

    /// A path such as <c>brute/garbage/brute_helmet</c> is a piece stored under a
    /// character that already has its own folder. The list shows the character only.
    private static bool IsChildFolder(string folder, HashSet<string> present)
    {
        int slash = folder.IndexOf('/');
        return slash > 0 && present.Contains(folder[..slash]);
    }

    private static int KindRank(string kind) => kind switch
    {
        "model" => 0,
        "physics_model" => 1,
        "collision_model" => 2,
        _ => 9,
    };
}

public sealed class CharacterModelCategory
{
    public CharacterModelCategory(string name, string folder)
    {
        Name = name;
        Folder = folder;
    }

    public string Name { get; }
    public string Folder { get; }
}

