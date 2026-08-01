using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace HaloMeister.App.Pages;

public sealed partial class CommunityPage : Page
{
    public CommunityPage()
    {
        InitializeComponent();
        VersionText.Text = L.Format("community.version_number", ReleaseUpdateService.Current.CurrentVersion);
    }

    private async void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string target } ||
            !Uri.TryCreate(target, UriKind.Absolute, out Uri? uri))
            return;

        await Launcher.LaunchUriAsync(uri);
    }
}
