using Avalonia.Markup.Xaml;

namespace SimpleXray.App.Localization;

/// <summary>
/// XAML usage: <c>Text="{loc:Loc Action_Connect}"</c> with
/// <c>xmlns:loc="using:SimpleXray.App.Localization"</c>.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
