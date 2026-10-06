using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace HaloMeister.App.Pages;

public sealed partial class MusicPage : Page, IActivatablePage
{
    private readonly MusicLibraryService _music = MusicLibraryService.Current;
    private readonly MediaPlayer _player = new() { IsLoopingEnabled = false };
    private bool _following;
    private bool _trackClock;
    private long _clockStamp;
    private double _clockSeconds;
    private double _lastActual = -1;
    private int _playbackEpoch;
    private readonly Dictionary<string, MusicFile> _previewCache = new(StringComparer.Ordinal);
    private readonly ObservableCollection<MusicCategory> _categories = [];
    private readonly List<MusicRow> _variantRows = [];
    private readonly ObservableCollection<MusicRow> _permutations = [];
    private string? _variantTitle;
    private List<MusicTrack> _all = [];
    private List<MusicGroup> _groups = [];
    private string? _categoryFolder;
    private MusicTrack? _selected;
    private MusicRow? _activeRow;
    private MusicFile? _activeFile;
    private string? _activePackage;
    private string? _readyPackage;
    private string? _activeLanguage;
    private string? _readyLanguage;
    private int _activeIndex = -1;
    private int _readyIndex = -1;
    private bool _loadingPlay;
    private string? _language;
    private string? _dialogueLanguage;
    private string _catalog = "music";
    private int _resolveVersion;
    private int _playRequest;
    private int _busy;
    private bool _scanned;
    private bool _suppressLanguage;
    private bool _suppressSelection;
    private bool _scrubbing;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private int _waveVersion;
    private float[] _peaks = [];
    private bool _suppressVolume = true;
    private double _volumeBeforeMute = 1;

    public MusicPage()
    {
        InitializeComponent();
        CategoryList.ItemsSource = _categories;
        PermutationList.ItemsSource = _permutations;
        _player.AutoPlay = false;
        _player.MediaEnded += OnMediaEnded;
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        _toastTimer.Tick += OnToastTick;
        ExportTrackButton.IsEnabled = false;
        CueText.Text = L.Get("music.select_track");
        ShowIdlePlayer();
        LoadVolume();
        _dialogueLanguage = LoadStoredLanguage();
        _suppressVolume = false;
    }

    public void OnActivated()
    {
        if (_scanned)
            return;
        _scanned = true;
        _ = ScanAsync(refresh: false);
    }

    public void OnDeactivated()
    {
        if (_trackClock || _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            PauseActive();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await ScanAsync(refresh: true);

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyVariantFilter();

    private void OnCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
            return;

        string? folder = (CategoryList.SelectedItem as MusicCategory)?.Folder;
        if (string.Equals(folder, _categoryFolder, StringComparison.OrdinalIgnoreCase))
            return;

        _categoryFolder = folder;
        _ = LoadCategoryAsync(folder);
    }

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
            return;
        string catalog = (item.Tag as string) switch
        {
            "dialogue" => "dialogue",
            "vehicle" => "vehicle",
            "weapon" => "weapon",
            "character" => "character",
            "dialog" => "dialog",
            "sandbox" => "sandbox",
            "device" => "device",
            "levels" => "levels",
            "materials" => "materials",
            "ui" => "ui",
            "visual_fx" => "visual_fx",
            _ => "music",
        };
        if (catalog == _catalog)
            return;

        _catalog = catalog;
        _categoryFolder = null;
        _selected = null;
        _language = IsDialogue ? _dialogueLanguage : null;
        _variantRows.Clear();
        _variantTitle = null;
        _permutations.Clear();
        ExportTrackButton.IsEnabled = false;
        if (!string.IsNullOrEmpty(SearchBox.Text))
            SearchBox.Text = "";
        CueText.Text = L.Get(SelectPromptKey);
        LanguageHost.Visibility = IsDialogue && LanguageBox.Items.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyCatalogChrome();
        ApplyFilter();
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguage || string.IsNullOrEmpty(_categoryFolder) || LanguageBox.SelectedItem is not string language)
            return;
        if (string.Equals(language, _language, StringComparison.OrdinalIgnoreCase))
            return;
        if (IsDialogue || IsDialog)
        {
            _dialogueLanguage = language;
            SaveLanguage(language);
        }
        _language = language;
        if (IsDialogue || IsDialog)
        {
            if (_activeRow is not null)
                await ToggleRowAsync(_activeRow);
            return;
        }

        await LoadCategoryAsync(_categoryFolder, replay: true);
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicRow row } || string.IsNullOrEmpty(row.Package))
            return;
        await ToggleRowAsync(row);
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolume)
            return;
        ApplyVolume(e.NewValue, persist: true);
    }

    private void OnVolumeMute(object sender, RoutedEventArgs e)
    {
        if (_player.IsMuted || _player.Volume <= 0.001)
        {
            double restore = _volumeBeforeMute > 0.001 ? _volumeBeforeMute : 1;
            SetVolumeSlider(restore * 100);
            return;
        }

        _volumeBeforeMute = _player.Volume;
        SetVolumeSlider(0);
    }

    private void LoadVolume()
    {
        double percent = 100;
        try
        {
            string path = VolumeStorePath();
            if (File.Exists(path) &&
                double.TryParse(File.ReadAllText(path).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double saved))
                percent = Math.Clamp(saved, 0, 100);
        }
        catch (Exception)
        {
            percent = 100;
        }

        SetVolumeSlider(percent);
    }

    private void SetVolumeSlider(double percent)
    {
        _suppressVolume = true;
        VolumeSlider.Value = Math.Clamp(percent, 0, 100);
        _suppressVolume = false;
        ApplyVolume(VolumeSlider.Value, persist: true);
    }

    private void ApplyVolume(double percent, bool persist)
    {
        double volume = Math.Clamp(percent, 0, 100) / 100;
        _player.IsMuted = volume <= 0;
        _player.Volume = volume <= 0 ? 0 : volume;
        VolumeIcon.Glyph = volume <= 0 ? "\uE74F" : "\uE767";
        ToolTipService.SetToolTip(VolumeButton, L.Get(volume <= 0 ? "music.unmute" : "music.mute"));
        if (!persist)
            return;
        try
        {
            string path = VolumeStorePath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, percent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            // Remembering the volume must not interrupt playback.
        }
    }

    private static string VolumeStorePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloMeister",
            "music-volume.txt");

    private static string? LoadStoredLanguage()
    {
        try
        {
            string path = LanguageStorePath();
            if (!File.Exists(path))
                return null;
            string saved = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(saved) ? null : saved;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void SaveLanguage(string language)
    {
        try
        {
            string path = LanguageStorePath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, language);
        }
        catch (Exception)
        {
            // Remembering the language must not interrupt playback.
        }
    }

    private static string LanguageStorePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloMeister",
            "music-language.txt");

    private void OnTransport(object sender, RoutedEventArgs e)
    {
        if (_player.Source is null || _readyIndex < 0)
            return;
        if (_trackClock)
            PauseActive();
        else
            ResumeActive();
    }

    private async Task ToggleRowAsync(MusicRow row)
    {
        if (string.IsNullOrEmpty(row.Package))
            return;

        bool sameClip = _player.Source is not null &&
            string.Equals(_readyPackage, row.Package, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_readyLanguage ?? "", _language ?? "", StringComparison.OrdinalIgnoreCase) &&
            _readyIndex == row.Index;

        if (sameClip && _trackClock)
        {
            PauseActive();
            return;
        }

        if (sameClip)
        {
            ResumeActive();
            return;
        }

        if (_loadingPlay && IsActive(row) &&
            string.Equals(_activeLanguage ?? "", _language ?? "", StringComparison.OrdinalIgnoreCase))
            return;

        int request = ++_playRequest;
        string? language = _language;
        MarkActive(row, row.Package);
        _loadingPlay = true;
        try
        {
            MusicFile file = await GetPreviewAsync(row.Package, language, row);
            if (request != _playRequest)
                return;

            _playbackEpoch++;
            var previous = _player.Source as MediaSource;
            _player.Source = MediaSource.CreateFromUri(new Uri(file.Path));
            previous?.Dispose();
            _readyPackage = row.Package;
            _readyLanguage = language;
            _readyIndex = row.Index;
            _activeFile = file;
            ShowActivePlayer(SelectedCategoryName(), row, file);
            _player.Play();
            ArmClock(0);
        }
        catch (Exception)
        {
            if (request != _playRequest)
                return;
            ClearActive();
            ShowIdlePlayer();
            ShowStatus(L.Get("music.play_failed"), InfoBarSeverity.Error);
        }
        finally
        {
            if (request == _playRequest)
                _loadingPlay = false;
        }
    }

    private async void OnExportOne(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicRow row } || string.IsNullOrEmpty(row.Package) || !TryEnter())
            return;

        string scratch = NewScratch();
        string fileName = Sanitize(row.Name) + ".wav";
        string temp = Path.Combine(scratch, fileName);
        MusicFile exported;
        try
        {
            exported = await _music.ExportAsync(row.Package, _language, row.Index, temp);
        }
        catch (Exception)
        {
            DeleteScratch(scratch);
            ShowStatus(L.Get("music.export_clip_failed"), InfoBarSeverity.Error);
            return;
        }
        finally
        {
            Exit();
        }

        string? folder = await ChooseFolderAsync();
        if (string.IsNullOrEmpty(folder))
        {
            DeleteScratch(scratch);
            return;
        }

        try
        {
            File.Copy(temp, Path.Combine(folder, fileName), overwrite: true);
            ShowStatus(
                L.Format("music.exported_one", $"{exported.Name} · {FormatDuration(exported)}"),
                InfoBarSeverity.Success);
        }
        catch (Exception)
        {
            ShowStatus(L.Get("music.export_clip_failed"), InfoBarSeverity.Error);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    private async void OnExportTrack(object sender, RoutedEventArgs e)
    {
        if (_variantRows.Count == 0 || !TryEnter())
            return;

        string scratch = NewScratch();
        var ready = new List<string>();
        int failed = 0;
        try
        {
            for (int i = 0; i < _variantRows.Count; i++)
            {
                MusicRow row = _variantRows[i];
                string fileName = $"{i + 1:000}_{Sanitize(row.Name)}.wav";
                try
                {
                    await _music.ExportAsync(row.Package, _language, row.Index, Path.Combine(scratch, fileName));
                    ready.Add(fileName);
                }
                catch (Exception)
                {
                    failed++;
                }
            }
        }
        finally
        {
            Exit();
        }

        if (ready.Count == 0)
        {
            DeleteScratch(scratch);
            ShowStatus(L.Get("music.export_clip_failed"), InfoBarSeverity.Error);
            return;
        }

        string? folder = await ChooseFolderAsync();
        if (string.IsNullOrEmpty(folder))
        {
            DeleteScratch(scratch);
            return;
        }

        int exported = 0;
        foreach (string fileName in ready)
        {
            try
            {
                File.Copy(Path.Combine(scratch, fileName), Path.Combine(folder, fileName), overwrite: true);
                exported++;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        DeleteScratch(scratch);
        string message = failed == 0
            ? L.Format("music.exported", exported.ToString(), folder)
            : exported == 0
                ? L.Get("music.export_clip_failed")
                : L.Format(
                    "music.exported_partial",
                    exported.ToString(),
                    failed.ToString(),
                    folder);
        ShowStatus(
            message,
            exported == 0 ? InfoBarSeverity.Error : InfoBarSeverity.Success);
    }

    private async Task<string?> ChooseFolderAsync()
    {
        try
        {
            return await PickFolderAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(ExportError(ex), InfoBarSeverity.Error);
            return null;
        }
    }

    private async Task ScanAsync(bool refresh)
    {
        if (!TryEnter())
            return;

        EmptyState.Visibility = Visibility.Collapsed;
        try
        {
            IReadOnlyList<MusicTrack> tracks = refresh
                ? await _music.RefreshAsync()
                : await _music.ListAsync();
            _all = tracks.ToList();
            _categoryFolder = null;
            _selected = null;
            _language = null;
            _variantRows.Clear();
            _variantTitle = null;
            _permutations.Clear();
            ExportTrackButton.IsEnabled = false;
            CueText.Text = L.Get(SelectPromptKey);
            if (!IsDialogue)
                LanguageHost.Visibility = Visibility.Collapsed;
            ApplyCatalogChrome();
            ApplyFilter();
            if (_all.Count > 0)
                HideToast();
        }
        catch (Exception ex)
        {
            _scanned = false;
            _all = [];
            _groups = [];
            _categoryFolder = null;
            _categories.Clear();
            _variantRows.Clear();
            _variantTitle = null;
            _permutations.Clear();
            EmptyState.Visibility = Visibility.Visible;
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Exit();
        }
    }

    private async Task LoadCategoryAsync(string? folder, bool replay = false)
    {
        int version = ++_resolveVersion;
        int replayIndex = _activeIndex;
        string? replayPackage = _activePackage;
        MusicGroup? group = _groups.FirstOrDefault(item =>
            string.Equals(item.Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            ClearCue();
            return;
        }

        _variantRows.Clear();
        _variantTitle = group.Name;
        _permutations.Clear();
        ExportTrackButton.IsEnabled = false;
        CueText.Text = L.Get("music.resolving");
        try
        {
            if (IsDialogue)
                await LoadDialogueAsync(group, version);
            else if (IsDialog)
                await LoadDialogAsync(group, version);
            else if (IsFolderCatalog)
                LoadFolderRows(group);
            else
                await LoadMusicAsync(group, version);
            if (version != _resolveVersion)
                return;

            ApplyVariantFilter();
            if (replay && _variantRows.Count > 0)
            {
                MusicRow row = _variantRows.FirstOrDefault(item => IsSame(item, replayPackage, replayIndex))
                    ?? _variantRows[0];
                await ToggleRowAsync(row);
            }
        }
        catch (Exception ex)
        {
            if (version != _resolveVersion)
                return;
            _variantRows.Clear();
            _permutations.Clear();
            ExportTrackButton.IsEnabled = false;
            CueText.Text = L.Get(NoMediaKey);
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task LoadMusicAsync(MusicGroup group, int version)
    {
        string play = L.Get("music.play");
        foreach (MusicTrack track in group.Tracks)
        {
            MusicCue cue = await _music.ResolveAsync(track.Package, _language);
            if (version != _resolveVersion)
                return;
            if (cue.Permutations.Count == 0)
                continue;

            _selected = track;
            _language = cue.Language;
            ShowLanguages(cue);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MusicPermutation permutation in cue.Permutations)
            {
                string key = permutation.MediaId != 0
                    ? "id:" + permutation.MediaId
                    : "loc:" + permutation.Location;
                if (!seen.Add(key))
                    continue;
                _variantRows.Add(new MusicRow(permutation, play, track.Package));
            }

            return;
        }

        _selected = null;
        LanguageHost.Visibility = Visibility.Collapsed;
    }

    private async Task LoadDialogueAsync(MusicGroup group, int version)
    {
        if (group.Tracks.Count == 0)
            return;

        string? preferred = _dialogueLanguage ?? DefaultDialogueLanguage();
        MusicCue cue = await _music.ResolveAsync(group.Tracks[0].Package, preferred);
        if (version != _resolveVersion)
            return;

        _selected = group.Tracks[0];
        _language = string.IsNullOrEmpty(cue.Language) ? preferred : cue.Language;
        if (string.IsNullOrEmpty(_dialogueLanguage))
            _dialogueLanguage = _language;
        ShowLanguages(cue);
        IReadOnlyDictionary<string, IReadOnlyList<int>> playable = await _music.PlayableIndicesAsync(
            group.Tracks.Select(track => track.Package).ToArray(),
            _language);
        if (version != _resolveVersion)
            return;

        string play = L.Get("music.play");
        foreach (MusicTrack track in group.Tracks)
        {
            if (!playable.TryGetValue(track.Package, out IReadOnlyList<int>? indices) || !indices.Contains(0))
                continue;
            var permutation = new MusicPermutation(0, track.Name, _language ?? "", "", 0);
            _variantRows.Add(new MusicRow(permutation, play, track.Package));
        }
    }

    private void ShowLanguages(MusicCue cue)
    {
        bool same = LanguageBox.Items.Count == cue.Languages.Count;
        if (same)
        {
            for (int i = 0; i < cue.Languages.Count; i++)
            {
                if (!string.Equals(LanguageBox.Items[i] as string, cue.Languages[i], StringComparison.OrdinalIgnoreCase))
                {
                    same = false;
                    break;
                }
            }
        }

        _suppressLanguage = true;
        if (!same)
            LanguageBox.ItemsSource = cue.Languages.ToList();
        LanguageBox.SelectedItem = cue.Languages.FirstOrDefault(item =>
            item.Equals(cue.Language, StringComparison.OrdinalIgnoreCase));
        LanguageHost.Visibility = IsDialogue
            ? (cue.Languages.Count > 0 ? Visibility.Visible : Visibility.Collapsed)
            : (cue.Languages.Count > 1 ? Visibility.Visible : Visibility.Collapsed);
        _suppressLanguage = false;
    }

    private void ApplyVariantFilter()
    {
        string query = SearchBox.Text.Trim();
        _permutations.Clear();
        foreach (MusicRow row in _variantRows)
        {
            if (query.Length == 0 ||
                row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Location.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.PathText.Contains(query, StringComparison.OrdinalIgnoreCase))
                _permutations.Add(row);
        }

        ExportTrackButton.IsEnabled = Volatile.Read(ref _busy) == 0 && _variantRows.Count > 0;
        if (string.IsNullOrEmpty(_categoryFolder))
            return;
        if (_variantRows.Count == 0)
        {
            CueText.Text = L.Get(NoMediaKey);
            return;
        }

        string shown = _permutations.Count == _variantRows.Count
            ? _variantRows.Count.ToString()
            : $"{_permutations.Count}/{_variantRows.Count}";
        CueText.Text = L.Format(
            RecordsKey,
            shown,
            _variantTitle ?? SelectedCategoryName());
        ApplyActiveLabel();
    }

    private void ApplyFilter()
    {
        List<MusicTrack> catalog = CatalogTracks().ToList();
        _groups = TopFolderRoot is { } root
            ? GroupByTopFolder(catalog, root)
            : catalog
                .GroupBy(track => track.Folder, StringComparer.OrdinalIgnoreCase)
                .Select(group => new MusicGroup(
                    group.Key,
                    CategoryName(group.Key),
                    group.OrderBy(track => track.Name, StringComparer.OrdinalIgnoreCase).ToList()))
                .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        string? previous = _categoryFolder;
        _suppressSelection = true;
        _categories.Clear();
        foreach (MusicGroup group in _groups)
            _categories.Add(new MusicCategory(group.Folder, group.Name));

        MusicCategory? category = _categories.FirstOrDefault(item =>
                string.Equals(item.Folder, previous, StringComparison.OrdinalIgnoreCase))
            ?? _categories.FirstOrDefault();
        CategoryList.SelectedItem = category;
        _categoryFolder = category?.Folder;
        _suppressSelection = false;

        EmptyState.Visibility = catalog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!string.Equals(previous, _categoryFolder, StringComparison.OrdinalIgnoreCase))
            _ = LoadCategoryAsync(_categoryFolder);
    }

    private void ClearCue()
    {
        _selected = null;
        _language = IsDialogue ? _dialogueLanguage : null;
        _variantRows.Clear();
        _variantTitle = null;
        _permutations.Clear();
        ExportTrackButton.IsEnabled = false;
        CueText.Text = L.Get(SelectPromptKey);
        if (!IsDialogue || LanguageBox.Items.Count == 0)
            LanguageHost.Visibility = Visibility.Collapsed;
    }

    private static string CategoryName(string folder)
    {
        int split = folder.LastIndexOf('/');
        return split >= 0 && split < folder.Length - 1
            ? folder[(split + 1)..]
            : folder;
    }

    private string SelectedCategoryName()
    {
        if (CategoryList.SelectedItem is MusicCategory category && !string.IsNullOrEmpty(category.Name))
            return category.Name;
        return string.IsNullOrEmpty(_categoryFolder) ? "audio" : CategoryName(_categoryFolder);
    }

    private bool IsActive(MusicRow row) => IsSame(row, _activePackage, _activeIndex);

    private static bool IsSame(MusicRow row, string? package, int index) =>
        row.Index == index &&
        string.Equals(row.Package, package, StringComparison.OrdinalIgnoreCase);

    private bool IsDialogue => string.Equals(_catalog, "dialogue", StringComparison.Ordinal);

    private bool IsDialog => string.Equals(_catalog, "dialog", StringComparison.Ordinal);

    private bool IsFolderCatalog => TopFolderRoot is not null;

    private string? TopFolderRoot => _catalog switch
    {
        "vehicle" => "vehicles/",
        "weapon" => "weapons/",
        "character" => "characters/",
        "dialog" => "dialog/combat/",
        "sandbox" => "005_sandbox/",
        "device" => "device_machines/",
        "levels" => "levels/",
        "materials" => "materials/",
        "ui" => "ui/",
        "visual_fx" => "visual_fx/",
        _ => null,
    };

    private string SelectPromptKey => _catalog switch
    {
        "dialogue" => "dialogue.select",
        "vehicle" => "vehicle.select",
        "weapon" => "weapon.select",
        "character" => "character.select",
        "dialog" => "dialog.select",
        "sandbox" => "sandbox.select",
        "device" => "device.select",
        "levels" => "levels.select",
        "materials" => "materials.select",
        "ui" => "ui.select",
        "visual_fx" => "visual_fx.select",
        _ => "music.select_track",
    };

    private string NoMediaKey => _catalog switch
    {
        "dialogue" => "dialogue.no_media",
        "vehicle" => "vehicle.no_media",
        "weapon" => "weapon.no_media",
        "character" => "character.no_media",
        "dialog" => "dialog.no_media",
        "sandbox" => "sandbox.no_media",
        "device" => "device.no_media",
        "levels" => "levels.no_media",
        "materials" => "materials.no_media",
        "ui" => "ui.no_media",
        "visual_fx" => "visual_fx.no_media",
        _ => "music.no_media",
    };

    private string RecordsKey => _catalog switch
    {
        "dialogue" => "dialogue.lines",
        "vehicle" => "vehicle.records",
        "weapon" => "weapon.records",
        "character" => "character.records",
        "dialog" => "dialog.records",
        "sandbox" => "sandbox.records",
        "device" => "device.records",
        "levels" => "levels.records",
        "materials" => "materials.records",
        "ui" => "ui.records",
        "visual_fx" => "visual_fx.records",
        _ => "music.permutations",
    };

    private IEnumerable<MusicTrack> CatalogTracks() =>
        _all.Where(track => string.Equals(track.Kind, _catalog, StringComparison.OrdinalIgnoreCase));

    private void ApplyCatalogChrome()
    {
        string prefix = _catalog switch
        {
            "dialogue" => "dialogue",
            "vehicle" => "vehicle",
            "weapon" => "weapon",
            "character" => "character",
            "dialog" => "dialog",
            "sandbox" => "sandbox",
            "device" => "device",
            "levels" => "levels",
            "materials" => "materials",
            "ui" => "ui",
            "visual_fx" => "visual_fx",
            _ => "music",
        };
        SearchBox.PlaceholderText = L.Get(prefix + ".search");
        CategoryCaption.Text = L.Get(prefix + ".categories");
        VariantCaption.Text = L.Get(prefix == "music" ? "music.variants" : prefix + ".list");
        ExportTrackButton.Content = L.Get(prefix + ".export_track");
        EmptyTitle.Text = L.Get(prefix + ".empty_title");
        EmptyHint.Text = L.Get(prefix + ".empty_hint");
    }

    private void LoadFolderRows(MusicGroup group)
    {
        _selected = group.Tracks.FirstOrDefault();
        _language = null;
        LanguageHost.Visibility = Visibility.Collapsed;
        FillFolderRows(group);
    }

    private async Task LoadDialogAsync(MusicGroup group, int version)
    {
        if (group.Tracks.Count == 0)
            return;

        string? preferred = _dialogueLanguage ?? DefaultDialogueLanguage();
        MusicCue cue = await _music.ResolveAsync(group.Tracks[0].Package, preferred);
        if (version != _resolveVersion)
            return;

        _selected = group.Tracks[0];
        _language = string.IsNullOrEmpty(cue.Language) ? preferred : cue.Language;
        if (string.IsNullOrEmpty(_dialogueLanguage))
            _dialogueLanguage = _language;
        ShowLanguages(cue);
        FillFolderRows(group);
    }

    private void FillFolderRows(MusicGroup group)
    {
        string play = L.Get("music.play");
        _variantRows.Clear();
        _variantTitle = group.Name;
        foreach (MusicTrack track in group.Tracks
                     .OrderBy(item => item.Folder, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            string relative = RelativeFolder(track, group.Folder);
            string path = string.IsNullOrEmpty(relative)
                ? group.Name
                : group.Name + " > " + string.Join(
                    " > ",
                    relative.Split('/', StringSplitOptions.RemoveEmptyEntries));
            var permutation = new MusicPermutation(0, track.Name, "", "", 0);
            _variantRows.Add(new MusicRow(permutation, play, track.Package, path));
        }
    }

    private static string RelativeFolder(MusicTrack track, string categoryFolder)
    {
        if (track.Folder.Equals(categoryFolder, StringComparison.OrdinalIgnoreCase))
            return "";
        string prefix = categoryFolder + "/";
        return track.Folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? track.Folder[prefix.Length..]
            : "";
    }

    private static List<MusicGroup> GroupByTopFolder(List<MusicTrack> tracks, string root) =>
        tracks
            .GroupBy(track => TopFolder(track, root), StringComparer.OrdinalIgnoreCase)
            .Select(group => new MusicGroup(
                group.Key,
                CategoryName(group.Key),
                group
                    .OrderBy(track => track.Folder, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(track => track.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string TopFolder(MusicTrack track, string root)
    {
        if (!track.Folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return track.Folder;
        string rest = track.Folder[root.Length..];
        int slash = rest.IndexOf('/');
        return slash < 0 ? track.Folder : track.Folder[..(root.Length + slash)];
    }

    private static string DefaultDialogueLanguage() =>
        LocalizationService.Current.Language switch
        {
            LocalizationService.ChineseSimplified => "Chinese(PRC)",
            LocalizationService.Japanese => "Japanese",
            LocalizationService.Korean => "Korean",
            _ => "English(US)",
        };

    private async Task<MusicFile> GetPreviewAsync(string package, string? language, MusicRow row)
    {
        string key = $"{package}|{language}|{row.Index}";
        if (_previewCache.TryGetValue(key, out MusicFile? cached) && File.Exists(cached.Path))
            return cached;

        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloMeister",
            "music-preview");
        Directory.CreateDirectory(directory);
        string languageToken = Sanitize(string.IsNullOrWhiteSpace(language) ? "default" : language);
        string path = Path.Combine(directory, $"{Sanitize(row.Name)}-{languageToken}-{row.Index}.wav");
        ReleasePreviewFile(path);
        MusicFile file = await _music.ExportAsync(package, language, row.Index, path);
        _previewCache[key] = file;
        return file;
    }

    private void ReleasePreviewFile(string path)
    {
        if (_activeFile is null ||
            !string.Equals(_activeFile.Path, path, StringComparison.OrdinalIgnoreCase))
            return;

        var playing = _player.Source as MediaSource;
        _player.Pause();
        _player.Source = null;
        playing?.Dispose();
        _activeFile = null;
    }

    private void MarkActive(MusicRow row, string package)
    {
        _activeIndex = row.Index;
        _activePackage = package;
        _activeLanguage = _language;
        _activeRow = row;
        string action = L.Get("music.pause_action");
        string idle = L.Get("music.play");
        foreach (MusicRow item in _permutations)
            item.PlayLabel = IsSame(item, package, row.Index) ? action : idle;
        TransportButton.IsEnabled = true;
        TransportButton.Content = action;
    }

    private void ApplyActiveLabel()
    {
        if (_activeIndex < 0 || string.IsNullOrEmpty(_activePackage))
            return;

        string label = L.Get(_trackClock ? "music.pause_action" : "music.play");
        TransportButton.Content = label;
        foreach (MusicRow row in _permutations)
        {
            if (!IsActive(row))
                continue;
            row.PlayLabel = label;
            _activeRow = row;
        }
    }

    private void PauseActive()
    {
        _player.Pause();
        DisarmClock(DisplayedPosition());
        SetActiveLabel(L.Get("music.play"));
        TransportButton.Content = L.Get("music.play");
    }

    private void ResumeActive()
    {
        MediaPlaybackSession session = _player.PlaybackSession;
        double duration = _activeFile?.Seconds ?? 0;
        bool rewound = duration > 0 && session.Position.TotalSeconds >= duration - 0.05;
        if (rewound)
            session.Position = TimeSpan.Zero;
        _player.Play();
        SetActiveLabel(L.Get("music.pause_action"));
        TransportButton.IsEnabled = true;
        TransportButton.Content = L.Get("music.pause_action");
        ArmClock(rewound ? 0 : session.Position.TotalSeconds);
    }

    private void SetActiveLabel(string label)
    {
        if (_activeRow is not null && IsActive(_activeRow))
            _activeRow.PlayLabel = label;
        foreach (MusicRow row in _permutations)
        {
            if (IsActive(row))
                row.PlayLabel = label;
        }
    }

    private void ClearActive()
    {
        DisarmClock(DisplayedPosition());
        _player.Pause();
        _readyIndex = -1;
        _readyPackage = null;
        _readyLanguage = null;
        _activeIndex = -1;
        _activePackage = null;
        _activeLanguage = null;
        _activeRow = null;
        _activeFile = null;
        string idle = L.Get("music.play");
        foreach (MusicRow row in _permutations)
            row.PlayLabel = idle;
    }

    private void ShowActivePlayer(string cue, MusicRow row, MusicFile file)
    {
        NowPlayingTitle.Text = row.Name;
        if (!string.IsNullOrWhiteSpace(row.Source.Language))
            cue = $"{cue} · {row.Source.Language}";
        NowPlayingDetail.Text = L.Format(
            "music.player_detail",
            cue,
            FormatDuration(file));
        ElapsedText.Text = "0:00";
        DurationText.Text = Clock(file.Seconds);
        TransportButton.IsEnabled = true;
        TransportButton.Content = L.Get("music.pause_action");
        _ = ShowWaveformAsync(file.Path);
    }

    private void ShowIdlePlayer()
    {
        NowPlayingTitle.Text = L.Get("music.player_idle");
        NowPlayingDetail.Text = "";
        ElapsedText.Text = "0:00";
        DurationText.Text = "0:00";
        TransportButton.IsEnabled = false;
        TransportButton.Content = L.Get("music.play");
        ClearWaveform();
    }

    private void ArmClock(double seconds)
    {
        _trackClock = true;
        _clockSeconds = Math.Max(0, seconds);
        _clockStamp = Stopwatch.GetTimestamp();
        _lastActual = -1;
        ShowClock(_clockSeconds);
        if (_following)
            return;
        _following = true;
        CompositionTarget.Rendering += OnPlaybackFrame;
    }

    private void DisarmClock(double seconds)
    {
        _trackClock = false;
        _lastActual = -1;
        if (_following)
        {
            CompositionTarget.Rendering -= OnPlaybackFrame;
            _following = false;
        }

        _clockSeconds = Math.Max(0, seconds);
        ShowClock(_clockSeconds);
    }

    private double CurrentClock()
    {
        if (!_trackClock)
            return _clockSeconds;
        double predicted = _clockSeconds
            + (Stopwatch.GetTimestamp() - _clockStamp) / (double)Stopwatch.Frequency;
        double duration = _activeFile?.Seconds ?? 0;
        if (predicted < 0)
            predicted = 0;
        if (duration > 0 && predicted > duration)
            predicted = duration;
        return predicted;
    }

    private double DisplayedPosition()
    {
        double shown = CurrentClock();
        double actual = ReadPosition();
        return Math.Abs(actual - shown) < 0.5 ? actual : shown;
    }

    private double ReadPosition()
    {
        try
        {
            if (_player.Source is null)
                return 0;
            double actual = _player.PlaybackSession.Position.TotalSeconds;
            return double.IsNaN(actual) || actual < 0 ? 0 : actual;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_trackClock || _following || _activeFile is null)
                return;
            _following = true;
            CompositionTarget.Rendering += OnPlaybackFrame;
        });
    }

    private void OnPlaybackFrame(object? sender, object e)
    {
        if (!_trackClock || _activeFile is null || _scrubbing)
            return;

        double duration = _activeFile.Seconds;
        double predicted = _clockSeconds
            + (Stopwatch.GetTimestamp() - _clockStamp) / (double)Stopwatch.Frequency;
        if (predicted < 0)
            predicted = 0;

        double actual = ReadPosition();
        // Swapping the source (a language change) often reports Position stuck at 0,
        // or a brief Paused, while audio is already playing. Follow the wall clock
        // until Position itself starts moving, then correct only real drift.
        if (_lastActual >= 0 && actual > _lastActual + 0.02 && Math.Abs(actual - predicted) > 0.3)
        {
            _clockSeconds = actual;
            _clockStamp = Stopwatch.GetTimestamp();
            predicted = actual;
        }

        if (actual > 0.02)
            _lastActual = actual;

        if (duration > 0 && predicted >= duration)
        {
            FinishPlayback();
            return;
        }

        ShowClock(predicted);
    }

    private void ShowClock(double seconds)
    {
        ElapsedText.Text = Clock(seconds);
        MovePlayhead(seconds);
    }

    private void FinishPlayback()
    {
        if (!_trackClock && !_following)
            return;
        DisarmClock(_activeFile?.Seconds ?? _clockSeconds);
        string play = L.Get("music.play");
        SetActiveLabel(play);
        TransportButton.Content = play;
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        int epoch = _playbackEpoch;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (epoch != _playbackEpoch || _activeFile is null || _player.Source is null || !_trackClock)
                return;
            double duration = _activeFile.Seconds;
            if (duration <= 0 || CurrentClock() + 0.25 < duration)
                return;
            FinishPlayback();
        });
    }

    private static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;
        int total = (int)seconds;
        return $"{total / 60}:{total % 60:00}";
    }

    private static string FormatDuration(MusicFile file)
    {
        int total = (int)Math.Round(file.Seconds);
        return $"{total / 60}:{total % 60:00} · {file.Channels} ch · {file.SampleRate} Hz";
    }

    private static string Sanitize(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        name = name.Trim().Trim('.');
        return string.IsNullOrEmpty(name) ? "music" : name;
    }

    private static string NewScratch()
    {
        string path = Path.Combine(Path.GetTempPath(), "HaloMeister", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteScratch(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp export must not block the next one.
        }
    }

    private static async Task<string?> PickFolderAsync()
    {
        if (MainWindow.Instance is not { } window)
            throw new InvalidOperationException(L.Get("music.export_failed"));

        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(window.AppWindow.Id);
        Microsoft.Windows.Storage.Pickers.PickFolderResult? result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    private static string ExportError(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? L.Get("music.export_failed") : ex.Message;

    private bool TryEnter()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return false;
        UpdateChrome();
        return true;
    }

    private void Exit()
    {
        Interlocked.Exchange(ref _busy, 0);
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        bool busy = Volatile.Read(ref _busy) == 1;
        BusyRing.IsActive = busy;
        RefreshButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        CategoryList.IsEnabled = !busy;
        ExportTrackButton.IsEnabled = !busy && _variantRows.Count > 0;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        ToastHost.Background = severity == InfoBarSeverity.Success
            ? (Brush)Application.Current.Resources["SystemFillColorSuccessBackgroundBrush"]
            : (Brush)Application.Current.Resources["SystemFillColorCriticalBackgroundBrush"];
        ToastText.Text = message;
        ToastHost.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnToastTick(object? sender, object e)
    {
        _toastTimer.Stop();
        HideToast();
    }

    private void HideToast()
    {
        ToastHost.Visibility = Visibility.Collapsed;
        _toastTimer.Stop();
    }

    private async Task ShowWaveformAsync(string path)
    {
        int version = ++_waveVersion;
        float[] peaks;
        try
        {
            peaks = await Task.Run(() => ReadPeaks(path, 180));
        }
        catch (IOException)
        {
            return;
        }

        if (version != _waveVersion)
            return;
        _peaks = peaks;
        RenderWaveform();
        WavePlayhead.Visibility = Visibility.Visible;
        ShowClock(CurrentClock());
    }

    private void ClearWaveform()
    {
        _waveVersion++;
        _peaks = [];
        WaveformCanvas.Children.Clear();
        WavePlayhead.Visibility = Visibility.Collapsed;
    }

    private void OnWaveformSized(object sender, SizeChangedEventArgs e)
    {
        RenderWaveform();
        ShowClock(CurrentClock());
    }

    private void OnWaveformPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CanScrub())
            return;
        _scrubbing = true;
        WaveformHost.CapturePointer(e.Pointer);
        ScrubTo(SecondsFromPointer(e), commit: false);
    }

    private void OnWaveformMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing)
            return;
        ScrubTo(SecondsFromPointer(e), commit: false);
    }

    private void OnWaveformReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing)
            return;
        _scrubbing = false;
        if (WaveformHost.PointerCaptures?.Any(capture => capture.PointerId == e.Pointer.PointerId) == true)
            WaveformHost.ReleasePointerCapture(e.Pointer);
        ScrubTo(SecondsFromPointer(e), commit: true);
    }

    private bool CanScrub() =>
        _player.Source is not null && _activeFile is not null && _activeFile.Seconds > 0;

    private double SecondsFromPointer(PointerRoutedEventArgs e)
    {
        double width = WaveformHost.ActualWidth;
        if (width <= 1 || _activeFile is null)
            return 0;
        double seconds = e.GetCurrentPoint(WaveformHost).Position.X / width * _activeFile.Seconds;
        return Math.Clamp(seconds, 0, _activeFile.Seconds);
    }

    private void ScrubTo(double seconds, bool commit)
    {
        ShowClock(seconds);
        if (!commit || _player.Source is null)
            return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
        if (_trackClock)
            ArmClock(seconds);
    }

    private void RenderWaveform()
    {
        WaveformCanvas.Children.Clear();
        double width = WaveformCanvas.ActualWidth;
        double height = WaveformCanvas.ActualHeight;
        if (width < 8 || height < 8 || _peaks.Length == 0)
            return;

        var brush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        double slot = width / _peaks.Length;
        for (int i = 0; i < _peaks.Length; i++)
        {
            double barHeight = Math.Max(2, _peaks[i] * (height - 6));
            double barWidth = Math.Max(1, slot * 0.72);
            var bar = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = brush,
                RadiusX = 1,
                RadiusY = 1,
            };
            Canvas.SetLeft(bar, i * slot + (slot - barWidth) / 2);
            Canvas.SetTop(bar, (height - barHeight) / 2);
            WaveformCanvas.Children.Add(bar);
        }
    }

    private void MovePlayhead(double seconds)
    {
        double width = WaveformCanvas.ActualWidth;
        double duration = _activeFile?.Seconds ?? 0;
        if (width <= 1 || duration <= 0)
            return;
        WavePlayheadShift.X = Math.Clamp(seconds / duration, 0, 1) * (width - WavePlayhead.Width);
    }

    private static float[] ReadPeaks(string path, int bars)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 44)
            return [];

        stream.Position = 12;
        int dataStart = -1;
        int dataLength = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            int size = reader.ReadInt32();
            if (id == "data")
            {
                dataStart = (int)stream.Position;
                dataLength = size;
                break;
            }

            stream.Position += size;
        }

        if (dataStart < 0 || dataLength < 2)
            return [];

        int samples = dataLength / 2;
        int bucket = Math.Max(1, samples / Math.Max(1, bars));
        var peaks = new List<float>(bars);
        stream.Position = dataStart;
        int remaining = samples;
        while (remaining > 0 && peaks.Count < bars)
        {
            int take = Math.Min(bucket, remaining);
            int peak = 0;
            for (int i = 0; i < take; i++)
            {
                int sample = Math.Abs((int)reader.ReadInt16());
                if (sample > peak)
                    peak = sample;
            }

            peaks.Add(peak / 32768f);
            remaining -= take;
        }

        float max = peaks.Count == 0 ? 0 : peaks.Max();
        if (max > 0.001f)
        {
            for (int i = 0; i < peaks.Count; i++)
                peaks[i] /= max;
        }

        return peaks.ToArray();
    }
}

public sealed class MusicCategory
{
    public MusicCategory(string folder, string name)
    {
        Folder = folder;
        Name = name;
    }

    public string Folder { get; }

    public string Name { get; }
}

public sealed record MusicGroup(string Folder, string Name, List<MusicTrack> Tracks);

public sealed class MusicRow : INotifyPropertyChanged
{
    private string _playLabel;

    public MusicRow(MusicPermutation source, string playLabel, string package, string? path = null)
    {
        Source = source;
        Package = package;
        PathText = path ?? "";
        _playLabel = playLabel;
    }

    public string PathText { get; }

    public Visibility PathVisibility =>
        string.IsNullOrWhiteSpace(PathText) ? Visibility.Collapsed : Visibility.Visible;

    public string Package { get; }

    public MusicPermutation Source { get; private set; }

    public void Apply(MusicPermutation source)
    {
        Source = source;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Location)));
    }

    public int Index => Source.Index;

    public string Name => Source.Name;

    public string Location => DisplayLocation(Source.Location);

    private static string DisplayLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return "";
        if (!location.Contains('.') && !location.Contains('\\'))
            return location;
        return FileName(location);
    }

    public Visibility LocationVisibility =>
        string.IsNullOrWhiteSpace(Location) ? Visibility.Collapsed : Visibility.Visible;

    private static string FileName(string location)
    {
        int split = Math.Max(location.LastIndexOf('/'), location.LastIndexOf('\\'));
        return split >= 0 && split < location.Length - 1
            ? location[(split + 1)..]
            : location;
    }

    public string PlayLabel
    {
        get => _playLabel;
        set
        {
            if (_playLabel == value)
                return;
            _playLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayLabel)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
