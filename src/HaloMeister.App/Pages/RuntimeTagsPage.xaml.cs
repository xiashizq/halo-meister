using System.Globalization;
using System.Text;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace HaloMeister.App.Pages;

public sealed partial class RuntimeTagsPage : Page
{
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly RuntimeTagModService _tagMods = new();
    private readonly NativeTagModExportService _nativeTagMods = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly RuntimeTagEditSessionService _editSessions;
    private readonly RuntimeTagViewState _state = new();
    private readonly DispatcherQueueTimer _tagFilterTimer;
    private IReadOnlyList<RuntimeTagEntry> _allTags = [];
    private IReadOnlyList<TreeViewNode> _fullTagTreeNodes = [];
    private RuntimeTagEntry? _selectedTag;
    private RuntimeTagFieldValue? _selectedField;
    private RuntimeTagEntry? _selectedReferenceTarget;
    private readonly Stack<FieldContext> _fieldHistory = new();
    private FieldContext? _fieldContext;
    private IReadOnlyList<RuntimeTagFieldValue> _contextFields = [];
    private IReadOnlyList<RuntimeTagFieldValue> _deepFields = [];
    private CancellationTokenSource? _fieldIndexCancellation;
    private bool _deepIndexReady;
    private bool _deepIndexTruncated;
    private byte[]? _rawSnapshot;
    private bool _busy;
    private bool _hasScanned;
    private bool _changingOpenTagSelection;
    private int _statusVersion;
    private readonly Dictionary<string, RuntimeTagModTag> _pendingModTags =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeTagEditSession> _tagEditSessions =
        new(StringComparer.OrdinalIgnoreCase);

    public RuntimeTagsPage()
    {
        InitializeComponent();
        _editSessions = new RuntimeTagEditSessionService(_memory);
        _tagFilterTimer = DispatcherQueue.CreateTimer();
        _tagFilterTimer.Interval = TimeSpan.FromMilliseconds(250);
        _tagFilterTimer.IsRepeating = false;
        _tagFilterTimer.Tick += OnTagFilterTimerTick;
        FieldList.ItemsSource = _state.Fields;
        StagedChangesList.ItemsSource = Array.Empty<RuntimeTagEditPatch>();
        _memory.ConnectionChanged += OnGameConnectionChanged;
        Unloaded += OnUnloaded;
        UpdateConnectionButtons();
    }

    private nint Hwnd => MainWindow.Instance is { } window
        ? WinRT.Interop.WindowNative.GetWindowHandle(window)
        : 0;

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            await Task.Run(() =>
            {
                if (_definitions.SchemaCount == 0)
                    _definitions.LoadDirectory(
                        RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
                if (!_definitions.HasSchema("weap"))
                    throw new InvalidDataException(
                        L.Get("runtime_tags.weap_schema_missing"));
                if (!_memory.IsConnected)
                    throw new InvalidOperationException(
                        L.Get("runtime_tags.connect_from_header"));
                _allTags = _memory.ReadTags();
            });
            _hasScanned = true;
            _fullTagTreeNodes = [];
            _tagEditSessions.Clear();
            ClearOpenTags();
            ApplyFilter();
            ConnectionText.Text = L.Format(
                "runtime_tags.scanned_connection",
                _allTags.Count.ToString("N0", CultureInfo.InvariantCulture),
                _definitions.SchemaCount.ToString("N0", CultureInfo.InvariantCulture));
            RefreshButton.IsEnabled = true;
            ShowStatus(L.Get("runtime_tags.scan_success"), InfoBarSeverity.Success);
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            _allTags = await Task.Run(_memory.ReadTags);
            _fullTagTreeNodes = [];
            ApplyFilter();
            RefreshOpenTags();
            ShowStatus(L.Format("runtime_tags.refreshed_tags", _allTags.Count.ToString("N0", CultureInfo.InvariantCulture)), InfoBarSeverity.Success);
        });
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _tagFilterTimer.Stop();
        if (SearchBox.Text.Trim().Length == 0)
        {
            ApplyFilter();
            return;
        }
        TagFilterStatusText.Text = L.Get("runtime_tags.waiting_for_typing");
        _tagFilterTimer.Start();
    }

    private void OnTagFilterTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            if (_fullTagTreeNodes.Count == 0)
                _fullTagTreeNodes = CreateTagTreeNodes(_allTags, false);
            ShowTagTreeNodes(_fullTagTreeNodes);
            TagFilterStatusText.Text = L.Format(
                "runtime_tags.tags_folder_cached",
                _allTags.Count.ToString("N0", CultureInfo.InvariantCulture));
            return;
        }

        RuntimeTagEntry[] matches = _allTags.Where(tag =>
                tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                tag.Group.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        const int resultLimit = 750;
        RuntimeTagEntry[] visible = matches.Take(resultLimit).ToArray();
        bool expand = visible.Length <= 400;
        ShowTagTreeNodes(CreateTagTreeNodes(visible, expand));
        TagFilterStatusText.Text = matches.Length > resultLimit
            ? L.Format(
                "runtime_tags.matches_showing_limit",
                matches.Length.ToString("N0", CultureInfo.InvariantCulture),
                resultLimit.ToString("N0", CultureInfo.InvariantCulture))
            : expand
                ? L.Format("runtime_tags.match_count", matches.Length.ToString("N0", CultureInfo.InvariantCulture))
                : L.Format("runtime_tags.branches_collapsed", matches.Length.ToString("N0", CultureInfo.InvariantCulture));
    }

    private void ShowTagTreeNodes(IEnumerable<TreeViewNode> nodes)
    {
        TagTree.RootNodes.Clear();
        foreach (TreeViewNode node in nodes) TagTree.RootNodes.Add(node);
    }

    private IReadOnlyList<TreeViewNode> CreateTagTreeNodes(
        IEnumerable<RuntimeTagEntry> tags,
        bool expandMatches)
    {
        var root = new TagFolder("");
        foreach (RuntimeTagEntry tag in tags)
        {
            string[] parts = tag.Name.Split(
                ['\\', '/'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            TagFolder folder = root;
            for (int i = 0; i < Math.Max(0, parts.Length - 1); i++)
            {
                if (!folder.Folders.TryGetValue(parts[i], out TagFolder? child))
                {
                    child = new TagFolder(parts[i]);
                    folder.Folders[parts[i]] = child;
                }
                folder = child;
            }
            string leaf = parts.Length > 0 ? parts[^1] : tag.Name;
            folder.Tags.Add((leaf, tag));
        }

        var nodes = new List<TreeViewNode>();
        foreach (TagFolder folder in root.Folders.Values.OrderBy(folder => folder.Name,
                     StringComparer.OrdinalIgnoreCase))
            nodes.Add(BuildFolderNode(folder, expandMatches));
        foreach ((string leaf, RuntimeTagEntry tag) in root.Tags
                     .OrderBy(item => item.Leaf, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Tag.Group, StringComparer.OrdinalIgnoreCase))
            nodes.Add(BuildTagNode(leaf, tag));
        return nodes;
    }

    private TreeViewNode BuildFolderNode(TagFolder folder, bool expanded)
    {
        int count = CountTags(folder);
        var node = new TreeViewNode
        {
            Content = new RuntimeTagTreeItem(folder.Name, L.Format("runtime_tags.folder_tag_count", count.ToString("N0", CultureInfo.InvariantCulture)), null),
            IsExpanded = expanded,
        };
        foreach (TagFolder child in folder.Folders.Values.OrderBy(item => item.Name,
                     StringComparer.OrdinalIgnoreCase))
            node.Children.Add(BuildFolderNode(child, expanded));
        foreach ((string leaf, RuntimeTagEntry tag) in folder.Tags
                     .OrderBy(item => item.Leaf, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Tag.Group, StringComparer.OrdinalIgnoreCase))
            node.Children.Add(BuildTagNode(leaf, tag));
        return node;
    }

    private TreeViewNode BuildTagNode(string leaf, RuntimeTagEntry tag)
        => new()
        {
            Content = new RuntimeTagTreeItem(
                $"{leaf}  [{tag.Group}]",
                _definitions.GetGroupDisplayName(tag.Group),
                tag),
        };

    private static int CountTags(TagFolder folder)
        => folder.Tags.Count + folder.Folders.Values.Sum(CountTags);

    private void OnTagTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        RuntimeTagTreeItem? item = args.InvokedItem as RuntimeTagTreeItem;
        if (item is null && args.InvokedItem is TreeViewNode node)
            item = node.Content as RuntimeTagTreeItem;
        if (item?.Tag is RuntimeTagEntry tag) OpenTag(tag);
    }

    private void OpenTag(RuntimeTagEntry tag)
    {
        string key = TagKey(tag);
        TabViewItem? tab = OpenTagsTabView.TabItems
            .OfType<TabViewItem>()
            .FirstOrDefault(item => item.Tag is RuntimeTagEntry open && TagKey(open) == key);
        if (tab is null)
        {
            tab = new TabViewItem
            {
                Header = $"{tag.LeafName} [{tag.Group}]",
                Tag = tag,
                IsClosable = true,
            };
            OpenTagsTabView.TabItems.Add(tab);
        }
        else
        {
            tab.Tag = tag;
        }

        TagEmptyState.Visibility = Visibility.Collapsed;
        EditorWorkspace.Visibility = Visibility.Visible;
        _changingOpenTagSelection = true;
        OpenTagsTabView.SelectedItem = tab;
        _changingOpenTagSelection = false;
        LoadSelectedTag(tag);
    }

    private void OnOpenTagSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingOpenTagSelection ||
            OpenTagsTabView.SelectedItem is not TabViewItem { Tag: RuntimeTagEntry tag })
            return;
        LoadSelectedTag(ResolveLiveTag(tag) ?? tag);
    }

    private void OnOpenTagCloseRequested(
        TabView sender,
        TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is TabViewItem { Tag: RuntimeTagEntry closing })
            _tagEditSessions.Remove(TagKey(closing));
        int index = sender.TabItems.IndexOf(args.Item);
        _changingOpenTagSelection = true;
        sender.TabItems.Remove(args.Item);
        if (sender.TabItems.Count == 0)
        {
            _changingOpenTagSelection = false;
            ClearTagEditor();
            return;
        }

        TabViewItem next = (TabViewItem)sender.TabItems[
            Math.Clamp(index, 0, sender.TabItems.Count - 1)];
        sender.SelectedItem = next;
        _changingOpenTagSelection = false;
        if (next.Tag is RuntimeTagEntry tag)
            LoadSelectedTag(ResolveLiveTag(tag) ?? tag);
    }

    private void RefreshOpenTags()
    {
        TabViewItem[] tabs = OpenTagsTabView.TabItems.OfType<TabViewItem>().ToArray();
        _changingOpenTagSelection = true;
        foreach (TabViewItem tab in tabs)
        {
            if (tab.Tag is not RuntimeTagEntry old || ResolveLiveTag(old) is not { } live)
            {
                OpenTagsTabView.TabItems.Remove(tab);
                continue;
            }
            tab.Tag = live;
            tab.Header = $"{live.LeafName} [{live.Group}]";
        }
        _changingOpenTagSelection = false;

        if (OpenTagsTabView.TabItems.Count == 0)
        {
            ClearTagEditor();
            return;
        }
        if (OpenTagsTabView.SelectedItem is not TabViewItem selected)
            OpenTagsTabView.SelectedItem = OpenTagsTabView.TabItems[0];
        else if (selected.Tag is RuntimeTagEntry tag)
            LoadSelectedTag(tag);
    }

    private RuntimeTagEntry? ResolveLiveTag(RuntimeTagEntry tag) =>
        _allTags.FirstOrDefault(candidate =>
            candidate.Group.Equals(tag.Group, StringComparison.OrdinalIgnoreCase) &&
            candidate.Name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase));

    private static string TagKey(RuntimeTagEntry tag) =>
        $"{tag.Group}\0{tag.Name}".ToUpperInvariant();

    private RuntimeTagEditSession GetOrCreateEditSession(RuntimeTagEntry tag)
    {
        string key = TagKey(tag);
        if (!_tagEditSessions.TryGetValue(key, out RuntimeTagEditSession? session))
        {
            session = new RuntimeTagEditSession(tag);
            _tagEditSessions[key] = session;
        }
        return session;
    }

    private RuntimeTagEditSession? SelectedEditSession() =>
        _selectedTag is { } tag && _tagEditSessions.TryGetValue(TagKey(tag), out RuntimeTagEditSession? session)
            ? session
            : null;

    private void UpdateEditSessionUi(RuntimeTagEditSession? session = null)
    {
        session ??= SelectedEditSession();
        IReadOnlyCollection<RuntimeTagEditPatch> patches = session?.Patches
            ?? Array.Empty<RuntimeTagEditPatch>();
        StagedChangesList.ItemsSource = patches;
        StagedChangesList.Visibility = patches.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        bool isSupported = _editSessions.IsSupportedBuild;
        bool canCommit = !_busy && isSupported && patches.Count > 0;
        CommitStagedButton.IsEnabled = canCommit;
        DiscardStagedButton.IsEnabled = !_busy && patches.Count > 0;
        UndoCommitButton.IsEnabled = !_busy && isSupported && session?.CanUndo == true;
        EditSessionStatusText.Text = patches.Count == 0
            ? _editSessions.SupportMessage
            : L.Format("runtime_tags.staged_change_count", patches.Count);

        if (_selectedTag is not { } selected) return;
        TabViewItem? tab = OpenTagsTabView.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(item => item.Tag is RuntimeTagEntry tag && TagKey(tag) == TagKey(selected));
        if (tab is not null)
            tab.Header = $"{selected.LeafName} [{selected.Group}]{(patches.Count > 0 ? " *" : string.Empty)}";
    }

    private void ClearOpenTags()
    {
        _tagEditSessions.Clear();
        _changingOpenTagSelection = true;
        OpenTagsTabView.TabItems.Clear();
        _changingOpenTagSelection = false;
        ClearTagEditor();
    }

    private void ClearTagEditor()
    {
        _selectedTag = null;
        _selectedField = null;
        _fieldIndexCancellation?.Cancel();
        StagedChangesList.ItemsSource = Array.Empty<RuntimeTagEditPatch>();
        EditSessionStatusText.Text = L.Get("runtime_tags.no_staged_changes");
        CommitStagedButton.IsEnabled = false;
        UndoCommitButton.IsEnabled = false;
        DiscardStagedButton.IsEnabled = false;
        EditorWorkspace.Visibility = Visibility.Collapsed;
        TagEmptyState.Visibility = Visibility.Visible;
    }

    private void LoadSelectedTag(RuntimeTagEntry tag)
    {
        TagEmptyState.Visibility = Visibility.Collapsed;
        EditorWorkspace.Visibility = Visibility.Visible;
        _selectedTag = tag;
        RuntimeTagEditSession session = GetOrCreateEditSession(tag);
        uint runtimeDatum = RuntimeTagMemoryService.BuildRuntimeDatum(tag);
        _selectedField = null;
        _fieldHistory.Clear();
        _contextFields = [];
        _fieldIndexCancellation?.Cancel();
        _deepFields = [];
        _deepIndexReady = false;
        _deepIndexTruncated = false;
        FieldSearchBox.Text = "";
        FieldSearchStatusText.Text = "";
        SelectedTagText.Text = $"{tag.Name} [{tag.Group}]";
        SelectedTagDetail.Text = L.Format(
            "runtime_tags.tag_summary",
            _definitions.GetGroupDisplayName(tag.Group),
            tag.RootCount,
            _definitions.HasSchema(tag.Group)
                ? L.Get("runtime_tags.schema_available")
                : L.Get("runtime_tags.schema_unavailable"));
        SelectedTagTechnicalDetail.Text = L.Format(
            "runtime_tags.tag_technical_detail",
            tag.Index,
            $"0x{runtimeDatum:X8}",
            $"0x{tag.DataOffset:X8}",
            $"0x{tag.DataAddress:X}",
            $"0x{tag.DefinitionOffset:X8}",
            $"0x{tag.DefinitionAddress:X}");
        UpdateWeaponActions(tag);
        UpdateTagModActions();
        UpdateEditSessionUi(session);
        FieldValueBox.Text = "";
        FieldValueBox.IsEnabled = false;
        InjectFieldButton.IsEnabled = false;
        _state.Fields.Clear();
        FieldContextText.Text = L.Get("runtime_tags.root_label");
        BackBlockButton.IsEnabled = false;
        OpenBlockButton.IsEnabled = false;
        BlockElementBox.IsEnabled = false;

        if (tag.DataAddress == 0)
        {
            ShowStatus(L.Get("runtime_tags.no_resolvable_root"), InfoBarSeverity.Warning);
            return;
        }

        try
        {
            _fieldContext = new FieldContext(L.Get("runtime_tags.root_label"), null, tag.DataAddress, 0, []);
            LoadFieldContext();
            if (_state.Fields.Count == 0 && !_definitions.HasSchema(tag.Group))
                ShowStatus(
                    L.Format("runtime_tags.no_schema_for_group", tag.Group),
                    InfoBarSeverity.Warning);
            else if (_state.Fields.Count == 0)
                ShowStatus(
                    L.Format("runtime_tags.no_schema_fields", tag.Group),
                    InfoBarSeverity.Warning);
            ReadRaw();
            StartDeepFieldIndex(tag);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void UpdateWeaponActions(RuntimeTagEntry tag)
    {
        bool isWeapon = string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase);
        SpawnActionPanel.Visibility = isWeapon ? Visibility.Visible : Visibility.Collapsed;
        if (!isWeapon) return;

        ScriptingBridgeStatus status = _bridge.GetStatus();
        SpawnBridgeText.Text = status.Summary;
        SpawnTagButton.Content = L.Get("runtime_tags.experimental_spawn_label");
        SecondarySpawnActionButton.Visibility = Visibility.Visible;
        SpawnExplanationText.Text = L.Get("runtime_tags.experimental_spawn_explanation");
        SpawnTagButton.IsEnabled =
            !_busy &&
            EnableExperimentalSpawnCheckBox.IsChecked == true &&
            status.IsRuntimeReady &&
            !status.IsStale;
    }

    private void OnExperimentalSpawnConsentChanged(object sender, RoutedEventArgs e)
    {
        if (_selectedTag is { } tag) UpdateWeaponActions(tag);
    }

    private async void OnSpawnSelectedTag(object sender, RoutedEventArgs e)
    {
        if (_busy || _selectedTag is not { } tag ||
            !string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase))
            return;
        if (EnableExperimentalSpawnCheckBox.IsChecked != true)
        {
            ShowStatus(
                L.Get("runtime_tags.acknowledge_spawn_warning"),
                InfoBarSeverity.Warning);
            return;
        }

        _busy = true;
        SpawnTagButton.IsEnabled = false;
        try
        {
            uint runtimeDatum = RuntimeTagMemoryService.BuildRuntimeDatum(tag);
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.BlamSpawn,
                runtimeDatum.ToString("X8", CultureInfo.InvariantCulture));
            ShowStatus(result.Message, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            if (_selectedTag is { } selected) UpdateWeaponActions(selected);
        }
    }

    private void OnFieldSearchChanged(object sender, TextChangedEventArgs e)
        => ApplyFieldSearch();

    private void OnQuickFieldSearch(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string query }) FieldSearchBox.Text = query;
    }

    private void ApplyFieldSearch()
    {
        string query = FieldSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            ReplaceFields(_contextFields);
            FieldContextText.Text = _fieldContext?.Label ?? L.Get("runtime_tags.root_label");
            FieldSearchStatusText.Text = _deepIndexReady
                ? L.Format("runtime_tags.indexed_count", _deepFields.Count.ToString("N0", CultureInfo.InvariantCulture))
                : string.Empty;
            return;
        }

        IEnumerable<RuntimeTagFieldValue> source =
            _deepIndexReady ? _deepFields : _contextFields;
        IEnumerable<RuntimeTagFieldValue> matches =
            query.Equals("type:reference", StringComparison.OrdinalIgnoreCase)
                ? source.Where(field => field.IsTagReference)
                : source.Where(field =>
                    field.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    field.Type.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    field.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
        RuntimeTagFieldValue[] results = matches.Take(2000).ToArray();
        ReplaceFields(results);
        FieldContextText.Text = L.Get("runtime_tags.field_search_results");
        FieldSearchStatusText.Text = _deepIndexReady
            ? (_deepIndexTruncated
                ? L.Format("runtime_tags.index_capped", results.Length.ToString("N0", CultureInfo.InvariantCulture))
                : L.Format("runtime_tags.match_count", results.Length.ToString("N0", CultureInfo.InvariantCulture)))
            : L.Get("runtime_tags.indexing_nested");
    }

    private async void StartDeepFieldIndex(RuntimeTagEntry tag)
    {
        var cancellation = new CancellationTokenSource();
        _fieldIndexCancellation = cancellation;
        try
        {
            DeepFieldIndex index = await Task.Run(
                () => BuildDeepFieldIndex(tag, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested || _selectedTag?.Index != tag.Index) return;
            _deepFields = index.Fields;
            _deepIndexTruncated = index.Truncated;
            _deepIndexReady = true;
            ApplyFieldSearch();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cancellation.IsCancellationRequested) return;
            FieldSearchStatusText.Text = L.Get("runtime_tags.nested_index_unavailable");
            ShowStatus(L.Format("runtime_tags.could_not_index_nested", ex.Message), InfoBarSeverity.Warning);
        }
    }

    private DeepFieldIndex BuildDeepFieldIndex(RuntimeTagEntry tag, CancellationToken cancellation)
    {
        const int maxFields = 25_000;
        const int maxElementsPerBlock = 128;
        const int maxDepth = 10;
        var output = new List<RuntimeTagFieldValue>();
        var visited = new HashSet<(string Definition, long Address, int Element)>();
        bool truncated = false;

        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            tag.Group, tag.DataAddress, _memory.ReadBytes, ResolveOrNull);
        Visit(root, "", 0);
        return new DeepFieldIndex(output, truncated);

        void Visit(IReadOnlyList<RuntimeTagFieldValue> fields, string path, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (RuntimeTagFieldValue field in fields)
            {
                if (output.Count >= maxFields)
                {
                    truncated = true;
                    return;
                }

                output.Add(CloneField(field, path + field.Name));
                if (!field.CanOpenBlock || depth >= maxDepth) continue;
                int elements = Math.Min(field.ChildCount, maxElementsPerBlock);
                if (elements < field.ChildCount) truncated = true;
                for (int element = 0; element < elements; element++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var key = (field.ChildBlockDefinition!, field.ChildAddress, element);
                    if (!visited.Add(key)) continue;
                    IReadOnlyList<RuntimeTagFieldValue> children;
                    try
                    {
                        children = _definitions.ReadBlockFields(
                            tag.Group,
                            field.ChildBlockDefinition!,
                            field.ChildAddress,
                            element,
                            _memory.ReadBytes,
                            ResolveOrNull);
                    }
                    catch
                    {
                        continue;
                    }
                    Visit(children, $"{path}{field.Name}[{element}] / ", depth + 1);
                    if (output.Count >= maxFields) return;
                }
            }
        }
    }

    private static RuntimeTagFieldValue CloneField(RuntimeTagFieldValue field, string name)
        => new()
        {
            Name = name,
            Type = field.Type,
            Offset = field.Offset,
            Size = field.Size,
            Address = field.Address,
            Value = field.Value,
            CanWrite = field.CanWrite,
            AllowedTagGroups = field.AllowedTagGroups,
            ReferencedTagIndex = field.ReferencedTagIndex,
            ChildBlockDefinition = field.ChildBlockDefinition,
            ChildCount = field.ChildCount,
            ChildAddress = field.ChildAddress,
            ChildElementSize = field.ChildElementSize,
        };

    private void OnFieldSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedField = FieldList.SelectedItem as RuntimeTagFieldValue;
        FieldValueBox.Text = _selectedField?.Value ?? "";
        bool writable = _selectedField?.CanWrite == true;
        FieldValueBox.IsEnabled = writable;
        InjectFieldButton.IsEnabled = writable && !_busy;
        bool canOpen = _selectedField?.CanOpenBlock == true;
        OpenBlockButton.IsEnabled = canOpen;
        BlockElementBox.IsEnabled = canOpen;
        BlockElementBox.Value = 0;
        BlockElementBox.Maximum = canOpen ? _selectedField!.ChildCount - 1 : 0;

        bool isReference = _selectedField?.IsTagReference == true;
        TagReferencePanel.Visibility = isReference ? Visibility.Visible : Visibility.Collapsed;
        _selectedReferenceTarget = isReference
            ? _allTags.FirstOrDefault(tag => tag.Index == _selectedField!.ReferencedTagIndex)
            : null;
        TagReferencePicker.Text = _selectedReferenceTarget?.DisplayName ?? "";
        InjectReferenceButton.IsEnabled = false;
    }

    private void OnOpenBlock(object sender, RoutedEventArgs e)
    {
        if (_selectedField is not { CanOpenBlock: true } field || _fieldContext is null)
            return;
        if (double.IsNaN(BlockElementBox.Value)) return;
        int element = checked((int)BlockElementBox.Value);
        if (element < 0 || element >= field.ChildCount)
        {
            ShowStatus(L.Format("runtime_tags.choose_element_range", field.ChildCount - 1), InfoBarSeverity.Warning);
            return;
        }

        FieldSearchBox.Text = "";
        _fieldHistory.Push(_fieldContext);
        _fieldContext = new FieldContext(
            $"{field.Name} [{element}/{field.ChildCount - 1}]",
            field.ChildBlockDefinition,
            field.ChildAddress,
            element,
            [.. _fieldContext.Blocks, new RuntimeTagModBlockStep
            {
                Offset = field.Offset,
                Definition = field.ChildBlockDefinition!,
                Element = element,
                ElementSize = field.ChildElementSize,
            }]);
        LoadFieldContext();
    }

    private void OnBackBlock(object sender, RoutedEventArgs e)
    {
        if (_fieldHistory.Count == 0) return;
        FieldSearchBox.Text = "";
        _fieldContext = _fieldHistory.Pop();
        LoadFieldContext();
    }

    private void LoadFieldContext()
    {
        if (_selectedTag is null || _fieldContext is null) return;
        IReadOnlyList<RuntimeTagFieldValue> fields = _fieldContext.BlockDefinition is null
            ? _definitions.ReadRootFields(
                _selectedTag.Group,
                _fieldContext.Address,
                _memory.ReadBytes,
                ResolveOrNull)
            : _definitions.ReadBlockFields(
                _selectedTag.Group,
                _fieldContext.BlockDefinition,
                _fieldContext.Address,
                _fieldContext.ElementIndex,
                _memory.ReadBytes,
                ResolveOrNull);

        _selectedField = null;
        _selectedReferenceTarget = null;
        _contextFields = fields;
        ReplaceFields(fields);
        FieldContextText.Text = _fieldContext.Label;
        BackBlockButton.IsEnabled = _fieldHistory.Count > 0;
        OpenBlockButton.IsEnabled = false;
        BlockElementBox.IsEnabled = false;
        FieldValueBox.Text = "";
        FieldValueBox.IsEnabled = false;
        InjectFieldButton.IsEnabled = false;
        TagReferencePanel.Visibility = Visibility.Collapsed;
        InjectReferenceButton.IsEnabled = false;
    }

    private void ReplaceFields(IEnumerable<RuntimeTagFieldValue> fields)
    {
        _state.Fields.Clear();
        foreach (RuntimeTagFieldValue field in fields) _state.Fields.Add(field);
    }

    private long? ResolveOrNull(uint offset)
        => _memory.TryResolveOffset(offset, out long address) ? address : null;

    private void OnInjectField(object sender, RoutedEventArgs e)
    {
        if (_selectedField is null || !_selectedField.CanWrite || _selectedTag is null) return;
        try
        {
            RuntimeTagFieldValue field = _selectedField;
            byte[] bytes = _definitions.ParseValue(field, FieldValueBox.Text);
            StageEdit(field, bytes, null);
            ShowStatus(
                L.Format("runtime_tags.staged_field", field.Name, bytes.Length, field.AddressDisplay),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnTagReferenceTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput ||
            _selectedField is not { IsTagReference: true } field)
            return;

        string query = sender.Text.Trim();
        IEnumerable<RuntimeTagEntry> choices = _allTags.Where(tag =>
            _definitions.IsTagGroupCompatible(tag.Group, field.AllowedTagGroups) &&
            (query.Length == 0 ||
             tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             tag.Group.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             tag.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)));
        RuntimeTagEntry[] results = choices.Take(200).ToArray();
        sender.ItemsSource = results;

        _selectedReferenceTarget = results.FirstOrDefault(tag =>
            IsExactReferenceText(tag, query));
        InjectReferenceButton.IsEnabled = _selectedReferenceTarget is not null;
    }

    private void OnTagReferenceSuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        _selectedReferenceTarget = args.SelectedItem as RuntimeTagEntry;
        sender.Text = _selectedReferenceTarget?.DisplayName ?? "";
        InjectReferenceButton.IsEnabled = _selectedReferenceTarget is not null;
    }

    private void OnInjectReference(object sender, RoutedEventArgs e)
    {
        if (_selectedField is not { IsTagReference: true } field ||
            _selectedReferenceTarget is not { } target)
            return;

        try
        {
            if (!_definitions.IsTagGroupCompatible(
                    target.Group, field.AllowedTagGroups))
                throw new InvalidOperationException(
                    L.Format(
                        "runtime_tags.reference_not_compatible",
                        target.Group,
                        string.Join(", ", field.AllowedTagGroups.Select(group => $"[{group}]"))));

            byte[] reference = _memory.BuildTagReference(target);
            StageEdit(field, reference, target);
            ShowStatus(
                L.Format("runtime_tags.staged_reference", field.Name, target.Name, target.Group),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnExperimentalInjectReference(object sender, RoutedEventArgs e)
    {
        if (_selectedField is not { IsTagReference: true } field) return;

        var searchBox = new TextBox
        {
            Header = L.Get("runtime_tags.search_all_loaded_tags"),
            PlaceholderText = L.Get("runtime_tags.placeholder_search_tags"),
        };
        var resultList = new ListView
        {
            Height = 420,
            SelectionMode = ListViewSelectionMode.Single,
            DisplayMemberPath = nameof(RuntimeTagEntry.DisplayName),
            ItemsSource = _allTags,
        };
        var warning = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = L.Get("runtime_tags.compatibility_disabled"),
            Message = L.Get("runtime_tags.compatibility_disabled_message"),
        };
        var content = new StackPanel
        {
            Width = 720,
            Spacing = 12,
        };
        content.Children.Add(warning);
        content.Children.Add(searchBox);
        content.Children.Add(resultList);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Format("runtime_tags.experimental_reference_swap", field.Name),
            Content = content,
            PrimaryButtonText = L.Get("runtime_tags.swap_without_check"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };

        void FilterResults(string query)
        {
            IEnumerable<RuntimeTagEntry> matches = _allTags;
            query = query.Trim();
            if (query.Length > 0)
            {
                matches = matches.Where(tag =>
                    tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    tag.Group.Contains(query.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ||
                    tag.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            resultList.ItemsSource = matches.ToArray();
            resultList.SelectedItem = null;
            dialog.IsPrimaryButtonEnabled = false;
        }

        searchBox.TextChanged += (_, _) => FilterResults(searchBox.Text);
        resultList.SelectionChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = resultList.SelectedItem is RuntimeTagEntry;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            resultList.SelectedItem is not RuntimeTagEntry target)
            return;

        try
        {
            byte[] reference = _memory.BuildTagReference(target);
            StageEdit(field, reference, target);
            ShowStatus(
                L.Format(
                    "runtime_tags.experimentally_staged_reference",
                    field.Name,
                    target.Name,
                    target.Group),
                InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void StageEdit(
        RuntimeTagFieldValue field,
        byte[] value,
        RuntimeTagEntry? referenceTarget)
    {
        RuntimeTagEditSession session = SelectedEditSession()
            ?? throw new InvalidOperationException("Open a runtime tag before staging an edit.");
        IReadOnlyList<RuntimeTagModBlockStep> blocks = _fieldContext?.Blocks
            .Select(step => new RuntimeTagModBlockStep
            {
                Offset = step.Offset,
                Definition = step.Definition,
                Element = step.Element,
                ElementSize = step.ElementSize,
            })
            .ToArray()
            ?? [];
        _editSessions.Stage(session, field, value, blocks, referenceTarget);
        UpdateEditSessionUi(session);
    }

    private async void OnCommitStaged(object sender, RoutedEventArgs e)
    {
        RuntimeTagEditSession? session = SelectedEditSession();
        if (session is null || !session.HasChanges) return;

        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Get("runtime_tags.commit_changes"),
            Content = L.Format("runtime_tags.commit_changes_confirm", session.Patches.Count),
            PrimaryButtonText = L.Get("runtime_tags.commit_changes"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        RuntimeTagEditPatch[] patches = session.Patches.ToArray();
        await RunBusy(async () =>
        {
            IReadOnlyList<RuntimeMemoryWrite> writes = await Task.Run(() => _editSessions.Commit(session));
            foreach (RuntimeTagEditPatch patch in patches)
                RecordPatch(patch.Field, patch.Value, patch.ReferenceTarget, patch.Blocks);
            FieldSearchBox.Text = "";
            if (_selectedTag is { } tag) LoadSelectedTag(tag);
            UpdateEditSessionUi(session);
            ShowStatus(L.Format("runtime_tags.committed_change_count", writes.Count), InfoBarSeverity.Success);
        });
    }

    private async void OnUndoCommit(object sender, RoutedEventArgs e)
    {
        RuntimeTagEditSession? session = SelectedEditSession();
        if (session?.CanUndo != true) return;

        await RunBusy(async () =>
        {
            IReadOnlyList<RuntimeMemoryWrite> writes = await Task.Run(() => _editSessions.Undo(session));
            if (_selectedTag is { } tag) LoadSelectedTag(tag);
            UpdateEditSessionUi(session);
            ShowStatus(L.Format("runtime_tags.undone_change_count", writes.Count), InfoBarSeverity.Success);
        });
    }

    private void OnDiscardStaged(object sender, RoutedEventArgs e)
    {
        RuntimeTagEditSession? session = SelectedEditSession();
        if (session is null || !session.HasChanges) return;
        session.Discard();
        UpdateEditSessionUi(session);
        ShowStatus(L.Get("runtime_tags.discarded_staged_changes"), InfoBarSeverity.Informational);
    }

    private static bool IsExactReferenceText(RuntimeTagEntry tag, string text)
    {
        if (tag.Name.Equals(text, StringComparison.OrdinalIgnoreCase) ||
            tag.DisplayName.Equals(text, StringComparison.OrdinalIgnoreCase))
            return true;

        // Accept a copied/pasted display path with any amount of whitespace
        // before the optional [fourCC] suffix.
        string suffix = $"[{tag.Group}]";
        if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        string path = text[..^suffix.Length].TrimEnd();
        return tag.Name.Equals(path, StringComparison.OrdinalIgnoreCase);
    }

    private void OnReadRaw(object sender, RoutedEventArgs e)
    {
        try { ReadRaw(); }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
    }

    private void ReadRaw()
    {
        if (_selectedTag is not { DataAddress: > 0 } tag) return;
        if (double.IsNaN(RawLengthBox.Value))
            throw new FormatException(L.Get("runtime_tags.enter_raw_byte_count"));
        int requested = checked((int)RawLengthBox.Value);
        int length = _definitions.GetRootSize(tag.Group) is int rootSize
            ? Math.Min(requested, rootSize)
            : requested;
        _rawSnapshot = _memory.ReadBytes(tag.DataAddress, length);
        RawHexBox.Text = FormatHex(_rawSnapshot);
    }

    private async void OnInjectRaw(object sender, RoutedEventArgs e)
    {
        if (_selectedTag is not { DataAddress: > 0 } tag || _rawSnapshot is null) return;
        byte[] bytes;
        try
        {
            bytes = ParseHex(RawHexBox.Text);
            if (bytes.Length != _rawSnapshot.Length)
                throw new FormatException(
                    L.Format("runtime_tags.raw_injection_exact_length", _rawSnapshot.Length, bytes.Length));
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            return;
        }

        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Format("runtime_tags.inject_raw_title", bytes.Length),
            Content = L.Format("runtime_tags.inject_raw_confirm", tag.DataAddress),
            PrimaryButtonText = L.Get("runtime_tags.inject_field"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _memory.WriteVerified(tag.DataAddress, bytes);
            _rawSnapshot = bytes;
            LoadSelectedTag(tag);
            ShowStatus(L.Format("runtime_tags.injected_raw_bytes", bytes.Length), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void RecordPatch(
        RuntimeTagFieldValue field,
        byte[] bytes,
        RuntimeTagEntry? referenceTarget,
        IReadOnlyList<RuntimeTagModBlockStep>? blocks = null)
    {
        if (_selectedTag is null || _fieldContext is null) return;
        string tagKey = $"{_selectedTag.Group}\0{_selectedTag.Name}";
        if (!_pendingModTags.TryGetValue(tagKey, out RuntimeTagModTag? modTag))
        {
            modTag = new RuntimeTagModTag
            {
                Group = _selectedTag.Group,
                Name = _selectedTag.Name,
            };
            _pendingModTags[tagKey] = modTag;
        }

        var patch = new RuntimeTagModPatch
        {
            Field = field.Name,
            Type = field.Type,
            Offset = field.Offset,
            Size = bytes.Length,
            Blocks = (blocks ?? _fieldContext!.Blocks).Select(step => new RuntimeTagModBlockStep
            {
                Offset = step.Offset,
                Definition = step.Definition,
                Element = step.Element,
                ElementSize = step.ElementSize,
            }).ToList(),
            Data = referenceTarget is null ? Convert.ToBase64String(bytes) : null,
            ReferenceGroup = referenceTarget?.Group,
            ReferenceName = referenceTarget?.Name,
        };
        string patchKey = GetPatchKey(patch);
        int existing = modTag.Patches.FindIndex(candidate =>
            GetPatchKey(candidate).Equals(patchKey, StringComparison.Ordinal));
        if (existing >= 0) modTag.Patches[existing] = patch;
        else modTag.Patches.Add(patch);
        UpdateTagModActions();
    }

    private static string GetPatchKey(RuntimeTagModPatch patch)
        => string.Join("/", patch.Blocks.Select(step =>
               $"{step.Offset:X}:{step.Definition}:{step.Element}:{step.ElementSize:X}")) +
           $"|{patch.Offset:X}:{patch.Size:X}";

    private void UpdateTagModActions()
    {
        int patchCount = _pendingModTags.Values.Sum(tag => tag.Patches.Count);
        SaveTagConfigButton.IsEnabled = !_busy && patchCount > 0;
        ClearTagConfigButton.IsEnabled = !_busy && patchCount > 0;
        SaveTagConfigButton.Content = patchCount > 0
            ? L.Format("runtime_tags.save_configuration_count", patchCount)
            : L.Get("runtime_tags.save_configuration");
        ExportTagModButton.IsEnabled = !_busy && patchCount > 0;
        ExportTagModButton.Content = patchCount > 0
            ? L.Format("runtime_tags.build_native_mod_count", patchCount)
            : L.Get("runtime_tags.build_native_mod");
        LoadTagModButton.IsEnabled =
            !_busy && _memory.IsConnected && _hasScanned && _allTags.Count > 0;
    }

    private async void OnSaveTagConfig(object sender, RoutedEventArgs e)
    {
        if (_pendingModTags.Count == 0) return;
        try
        {
            string leaf = _selectedTag?.LeafName ?? "tag-configuration";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"{SanitizeFileName(leaf)}-config",
            };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            picker.FileTypeChoices.Add(L.Get("runtime_tags.file_type_tag_config"), [".hmtagmod"]);
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            RuntimeTagModDocument document = CreateTagModDocument(
                Path.GetFileNameWithoutExtension(file.Name));
            await RunBusy(async () =>
            {
                await Task.Run(() => _tagMods.Save(document, file.Path));
                int patches = document.Tags.Sum(tag => tag.Patches.Count);
                ShowStatus(
                    L.Format(
                        "runtime_tags.saved_tag_config",
                        patches.ToString("N0", CultureInfo.InvariantCulture),
                        document.Tags.Count.ToString("N0", CultureInfo.InvariantCulture),
                        file.Path),
                    InfoBarSeverity.Success);
            });
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnClearTagConfig(object sender, RoutedEventArgs e)
    {
        int patchCount = _pendingModTags.Values.Sum(tag => tag.Patches.Count);
        if (patchCount == 0) return;
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Get("runtime_tags.clear_draft_title"),
            Content = L.Format(
                "runtime_tags.clear_draft_confirm",
                patchCount.ToString("N0", CultureInfo.InvariantCulture)),
            PrimaryButtonText = L.Get("runtime_tags.clear_draft"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        _pendingModTags.Clear();
        UpdateTagModActions();
        ShowStatus(
            L.Get("runtime_tags.cleared_draft"),
            InfoBarSeverity.Success);
    }

    private async void OnExportTagMod(object sender, RoutedEventArgs e)
    {
        if (_pendingModTags.Count == 0) return;
        try
        {
            string leaf = _selectedTag?.LeafName ?? "runtime-tags";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"{SanitizeFileName(leaf)}-WinGDK_P",
            };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            picker.FileTypeChoices.Add(L.Get("runtime_tags.file_type_iostore_overlay"), [".utoc"]);
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            RuntimeTagModDocument document = CreateTagModDocument(
                Path.GetFileNameWithoutExtension(file.Name));
            _busy = true;
            UpdateTagModActions();
            try
            {
                NativeTagModExportResult result = await _nativeTagMods.ExportAsync(
                    document, file.Path, _definitions.DirectoryPath);
                int patches = document.Tags.Sum(tag => tag.Patches.Count);
                ShowStatus(
                    L.Format(
                        "runtime_tags.built_native_mod",
                        patches.ToString("N0", CultureInfo.InvariantCulture),
                        document.Tags.Count.ToString("N0", CultureInfo.InvariantCulture),
                        result.UtocPath,
                        result.UcasPath,
                        result.PakPath,
                        result.SidecarPath),
                    InfoBarSeverity.Success);
            }
            finally
            {
                _busy = false;
                UpdateTagModActions();
            }
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnInstallOverlay(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            picker.FileTypeFilter.Add(".utoc");
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            string stem = Path.GetFileNameWithoutExtension(file.Name);
            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Format("runtime_tags.install_native_mod_title", stem),
                Content = L.Get("runtime_tags.install_native_mod_confirm"),
                PrimaryButtonText = L.Get("runtime_tags.install_native_mod"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            NativeTagModInstallResult result =
                await Task.Run(() => _nativeTagMods.InstallOverlay(file.Path));
            ShowStatus(
                L.Format("runtime_tags.installed_overlay", result.Name, result.PaksDirectory),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnLoadTagMod(object sender, RoutedEventArgs e)
    {
        if (!_memory.IsConnected || !_hasScanned || _allTags.Count == 0) return;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            picker.FileTypeFilter.Add(".hmtagmod");
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            RuntimeTagModDocument document =
                await Task.Run(() => _tagMods.Load(file.Path));
            int patchCount = document.Tags.Sum(tag => tag.Patches.Count);
            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Format("runtime_tags.apply_tag_config_title", document.Name),
                Content = L.Format(
                    "runtime_tags.tag_config_contains",
                    patchCount.ToString("N0", CultureInfo.InvariantCulture),
                    document.Tags.Count.ToString("N0", CultureInfo.InvariantCulture)),
                PrimaryButtonText = L.Get("runtime_tags.apply_configuration"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            await RunBusy(async () =>
            {
                RuntimeTagModApplyResult result = await Task.Run(
                    () => _tagMods.Apply(document, _allTags, _memory));
                if (_selectedTag is not null) LoadSelectedTag(_selectedTag);
                string missing = result.MissingTags.Count > 0
                    ? L.Format(
                        "runtime_tags.missing_tags_suffix",
                        result.MissingTags.Count.ToString("N0", CultureInfo.InvariantCulture),
                        string.Join(", ", result.MissingTags.Take(5)) +
                        (result.MissingTags.Count > 5 ? ", …" : string.Empty))
                    : string.Empty;
                ShowStatus(
                    L.Format(
                        "runtime_tags.applied_tag_config",
                        document.Name,
                        result.PatchCount.ToString("N0", CultureInfo.InvariantCulture),
                        result.TagCount.ToString("N0", CultureInfo.InvariantCulture),
                        missing),
                    result.MissingTags.Count > 0
                        ? InfoBarSeverity.Warning
                        : InfoBarSeverity.Success);
            });
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private RuntimeTagModDocument CreateTagModDocument(string name) => new()
    {
        Name = name,
        Tags = _pendingModTags.Values
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToList(),
    };

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character =>
            invalid.Contains(character) ? '-' : character).ToArray());
    }

    private async Task RunBusy(Func<Task> operation)
    {
        _busy = true;
        ScanButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        InjectFieldButton.IsEnabled = false;
        CommitStagedButton.IsEnabled = false;
        UndoCommitButton.IsEnabled = false;
        DiscardStagedButton.IsEnabled = false;
        try { await operation(); }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            UpdateConnectionButtons();
            InjectFieldButton.IsEnabled = _selectedField?.CanWrite == true;
            UpdateTagModActions();
            UpdateEditSessionUi();
        }
    }

    private void OnGameConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateConnectionButtons);

    private void UpdateConnectionButtons()
    {
        ScanButton.IsEnabled = !_busy && _memory.IsConnected;
        RefreshButton.IsEnabled = !_busy && _memory.IsConnected && _hasScanned;
        if (!_memory.IsConnected)
        {
            LoadTagModButton.IsEnabled = false;
            InjectFieldButton.IsEnabled = false;
        }
        UpdateTagModActions();
        UpdateEditSessionUi();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _tagFilterTimer.Stop();
        _fieldIndexCancellation?.Cancel();
        _memory.ConnectionChanged -= OnGameConnectionChanged;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        int version = ++_statusVersion;
        PageStatus.Title = severity == InfoBarSeverity.Error
            ? L.Get("runtime_tags.live_tag_error")
            : L.Get("runtime_tags.realtime_tags");
        PageStatus.Message = message;
        PageStatus.Severity = severity;
        PageStatus.IsOpen = true;
        if (severity == InfoBarSeverity.Success)
            _ = DismissSuccessAsync(version);
    }

    private async Task DismissSuccessAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (version == _statusVersion && PageStatus.Severity == InfoBarSeverity.Success)
            PageStatus.IsOpen = false;
    }

    private static string FormatHex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 3);
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            int count = Math.Min(16, bytes.Length - offset);
            text.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (int i = 0; i < count; i++)
                text.Append(bytes[offset + i].ToString("X2", CultureInfo.InvariantCulture))
                    .Append(' ');
            text.AppendLine();
        }
        return text.ToString();
    }

    private static byte[] ParseHex(string text)
    {
        var result = new List<byte>();
        foreach (string line in text.Replace("\r", "").Split('\n'))
        {
            string payload = line;
            int separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator >= 0 && line[..separator].Trim().Length == 8)
                payload = line[(separator + 2)..];
            foreach (string token in payload.Split(
                         [' ', '\t', ',', '-'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string hex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? token[2..]
                    : token;
                if (hex.Length != 2)
                    throw new FormatException(L.Format("runtime_tags.invalid_hex_byte", token));
                result.Add(byte.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }
        return result.ToArray();
    }

    private sealed record FieldContext(
        string Label,
        string? BlockDefinition,
        long Address,
        int ElementIndex,
        IReadOnlyList<RuntimeTagModBlockStep> Blocks);

    private sealed record DeepFieldIndex(
        IReadOnlyList<RuntimeTagFieldValue> Fields,
        bool Truncated);

    private sealed class TagFolder(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, TagFolder> Folders { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<(string Leaf, RuntimeTagEntry Tag)> Tags { get; } = [];
    }
}
