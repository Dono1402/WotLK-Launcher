using System.Windows;

namespace WotLK.Launcher.UI.V2.Localization;

/// <summary>Marks personal text that must retain its literal value in every interface language.</summary>
public static class LauncherLocalizationOptions
{
    public static readonly DependencyProperty IsUserTextProperty = DependencyProperty.RegisterAttached(
        "IsUserText", typeof(bool), typeof(LauncherLocalizationOptions),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsUserText(DependencyObject target) => (bool)target.GetValue(IsUserTextProperty);
    public static void SetIsUserText(DependencyObject target, bool value) => target.SetValue(IsUserTextProperty, value);
}
