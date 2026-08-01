using HaloMeister.App.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class SaveDataAlert : UserControl
{
    private readonly AppState _state = AppState.Current;
    private bool _subscribed;

    public SaveDataAlert()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
            return;
        _state.SaveLoaded += OnSaveLoaded;
        _subscribed = true;
        UpdateState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
            return;
        _state.SaveLoaded -= OnSaveLoaded;
        _subscribed = false;
    }

    private void OnSaveLoaded()
        => DispatcherQueue.TryEnqueue(UpdateState);

    private void UpdateState()
        => Alert.IsOpen = !_state.IsLoaded;
}
