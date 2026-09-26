using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Setup-page replacement for <c>x:Uid</c>. An <c>x:Uid</c> in this library resolves against the
/// library's own resource map, which holds no strings, so it never finds the Tray app's
/// <c>Strings\*\Resources.resw</c> entries and silently keeps the inline English text.
/// <c>setup:SetupText.Uid="Key"</c> applies the same <c>Key.Property</c> entries through
/// <see cref="SetupLocalization"/> when the element first loads, so, like <c>x:Uid</c>, the
/// resource wins over inline defaults. Unlike <c>x:Uid</c>, it applies only
/// <c>TextBlock.Text</c>, <c>InfoBar.Title</c> and <c>Message</c>, <c>Expander.Header</c>,
/// <c>ContentControl.Content</c>, and <c>AutomationProperties.Name</c> (keep
/// <c>SetupTextLocalizationTests</c> in sync), and a Uid set after the element has loaded is not
/// applied. Code must not also set a property that has a resource on the same element, because
/// the resource replaces a value set before the first load.
/// </summary>
public static class SetupText
{
    private const string AutomationNameProperty = "[using:Microsoft.UI.Xaml.Automation]AutomationProperties/Name";

    public static readonly DependencyProperty UidProperty = DependencyProperty.RegisterAttached(
        "Uid", typeof(string), typeof(SetupText), new PropertyMetadata(null, OnUidChanged));

    public static string GetUid(DependencyObject element) => (string)element.GetValue(UidProperty);

    public static void SetUid(DependencyObject element, string value) => element.SetValue(UidProperty, value);

    private static void OnUidChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.Loading -= ApplyOnFirstLoad;
        if (e.NewValue is string { Length: > 0 })
            element.Loading += ApplyOnFirstLoad;
    }

    private static void ApplyOnFirstLoad(FrameworkElement element, object args)
    {
        element.Loading -= ApplyOnFirstLoad;
        string uid = GetUid(element);
        switch (element)
        {
            case TextBlock text:
                Apply(uid, "Text", value => text.Text = value);
                break;
            case InfoBar infoBar:
                Apply(uid, "Title", value => infoBar.Title = value);
                Apply(uid, "Message", value => infoBar.Message = value);
                break;
            case Expander expander:
                Apply(uid, "Header", value => expander.Header = value);
                break;
            case ContentControl control:
                Apply(uid, "Content", value => control.Content = value);
                break;
        }

        Apply(uid, AutomationNameProperty, value => AutomationProperties.SetName(element, value));
    }

    private static void Apply(string uid, string property, Action<string> apply)
    {
        if (SetupLocalization.TryGetString($"{uid}/{property}") is { Length: > 0 } value)
            apply(value);
    }
}
