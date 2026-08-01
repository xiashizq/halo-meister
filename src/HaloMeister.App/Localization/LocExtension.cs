using Microsoft.UI.Xaml.Markup;

namespace HaloMeister.App.Localization;

/// <summary>
/// XAML usage: Text="{i18n:Loc Key='home.welcome'}"
/// Keys must be quoted — WinUI treats unquoted dotted tokens as type names.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue()
    {
        try
        {
            return L.Get(Key);
        }
        catch
        {
            return Key;
        }
    }
}
