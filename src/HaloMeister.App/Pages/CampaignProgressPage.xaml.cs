using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class CampaignProgressPage : Page
{
    public CampaignProgressPage()
    {
        InitializeComponent();
        SectionNav.SelectedItem = MissionsSection;
        SectionFrame.Navigate(typeof(MissionsPage));
    }

    private void OnSectionSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
            return;

        Type page = (item.Tag as string) switch
        {
            "skulls" => typeof(SkullsPage),
            "terminals" => typeof(CollectiblesPage),
            _ => typeof(MissionsPage),
        };

        if (SectionFrame.CurrentSourcePageType != page)
            SectionFrame.Navigate(page);
    }
}
