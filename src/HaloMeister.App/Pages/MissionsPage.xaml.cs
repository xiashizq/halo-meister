using HaloMeister.App.Models;
using HaloMeister.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class MissionsPage : Page
{
    private readonly AppState _state = AppState.Current;

    public MissionsPage()
    {
        InitializeComponent();
        MissionList.ItemsSource = _state.Missions;
    }

    private void OnCompleteAll(object sender, RoutedEventArgs e)
        => _state.SetAllTags(true, tag => tag.StartsWith(Catalog.CompletionPrefix, StringComparison.Ordinal));

    private void OnClearAll(object sender, RoutedEventArgs e)
        => _state.SetAllTags(false, tag =>
            tag.StartsWith(Catalog.CompletionPrefix, StringComparison.Ordinal)
            && !tag.Contains(".unlock_", StringComparison.Ordinal));

    private void OnUnlockGates(object sender, RoutedEventArgs e)
        => _state.SetAllTags(true, tag => tag.Contains(".unlock_", StringComparison.Ordinal));
}
