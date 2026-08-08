namespace HaloMeister.App.Pages;

/// <summary>
/// Cached pages keep their visual tree, but must refresh volatile state when shown again.
/// </summary>
public interface IActivatablePage
{
    void OnActivated();

    void OnDeactivated()
    {
    }
}
