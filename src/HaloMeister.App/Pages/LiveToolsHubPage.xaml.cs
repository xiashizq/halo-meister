using HaloMeister.App.Localization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HaloMeister.App.Pages;

public sealed partial class LiveToolsHubPage : Page
{
    private sealed record ToolDefinition(
        string LabelKey,
        Symbol Icon,
        Type PageType,
        bool Enabled = true);

    public LiveToolsHubPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Configure(e.Parameter as string ?? "live-gameplay");
    }

    private void Configure(string section)
    {
        (string titleKey, string descriptionKey, ToolDefinition[] tools) configuration = section switch
        {
            "live-spawn" => (
                "live_hub.spawn_title",
                "live_hub.spawn_desc",
                [
                    new("live_hub.builtin_mod", Symbol.Library, typeof(BuiltinModPage)),
                    new("live_hub.characters", Symbol.Add, typeof(SpawnerPage)),
                    new("live_hub.squads", Symbol.People, typeof(SquadsPage)),
                    new("live_hub.allegiance_demo", Symbol.Permissions, typeof(AllegianceDemoPage)),
                    new("live_hub.weapons", Symbol.Bullets, typeof(WeaponLoaderPage)),
                    new("live_hub.vehicles", Symbol.Map, typeof(VehicleWorkshopPage)),
                ]),
            "live-player" => (
                "live_hub.player_title",
                "live_hub.player_desc",
                [
                    new("live_hub.player_tools", Symbol.Map, typeof(PlayerToolsPage)),
                    new(
                        "live_hub.change_character_disabled",
                        Symbol.Contact,
                        typeof(ChangeBipedPage),
                        Enabled: false),
                    new("live_hub.armor_mixer", Symbol.Edit, typeof(ArmorMixerPage)),
                ]),
            "live-world" => (
                "live_hub.world_title",
                "live_hub.world_desc",
                [
                    new("live_hub.machinima", Symbol.Video, typeof(AdvancedMachinimaPage)),
                    new("live_hub.boundaries", Symbol.Map, typeof(BoundaryVolumesPage)),
                ]),
            _ => (
                "live_hub.gameplay_title",
                "live_hub.gameplay_desc",
                [
                    new("live_hub.gameplay_modifiers", Symbol.Repair, typeof(CheatGlobalsPage)),
                    new("live_hub.live_skulls", Symbol.Emoji, typeof(LiveSkullsPage)),
                ]),
        };

        (string titleKey, string descriptionKey, ToolDefinition[] tools) = configuration;
        HubTitle.Text = L.Get(titleKey);
        HubDescription.Text = L.Get(descriptionKey);
        ToolNav.MenuItems.Clear();

        NavigationViewItem? firstEnabled = null;
        foreach (ToolDefinition tool in tools)
        {
            bool enabled = tool.Enabled;
            var item = new NavigationViewItem
            {
                Content = L.Get(tool.LabelKey),
                Icon = new SymbolIcon(tool.Icon),
                Tag = tool.PageType,
                IsEnabled = enabled,
            };
            if (!enabled)
            {
                ToolTipService.SetToolTip(
                    item,
                    L.Get("live_hub.disabled_tooltip"));
            }
            ToolNav.MenuItems.Add(item);
            firstEnabled ??= enabled ? item : null;
        }

        if (firstEnabled is not null)
        {
            ToolNav.SelectedItem = firstEnabled;
            NavigateTo((Type)firstEnabled.Tag);
        }
    }

    private void OnToolSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: Type pageType })
            NavigateTo(pageType);
    }

    private void NavigateTo(Type pageType)
    {
        if (ToolFrame.CurrentSourcePageType != pageType)
            ToolFrame.Navigate(pageType);
    }
}
