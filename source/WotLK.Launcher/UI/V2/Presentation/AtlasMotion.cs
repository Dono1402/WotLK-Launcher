using System.Windows;
using System.Windows.Media.Animation;

namespace WotLK.Launcher.UI.V2.Presentation;

/// <summary>The same motion preference and timing vocabulary across native Atlas surfaces.</summary>
internal static class AtlasMotion
{
    // Scoped by the inactive WPF fixtures; production always follows Windows.
    internal static bool? EnabledOverride { get; set; }
    internal static bool IsEnabled => EnabledOverride ?? SystemParameters.ClientAreaAnimation;
    internal static TimeSpan PageDuration => TimeSpan.FromMilliseconds(200);
    internal static TimeSpan UniverseDuration => TimeSpan.FromMilliseconds(300);

    internal static void Reveal(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if (!IsEnabled || !element.IsLoaded || !element.IsVisible) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    internal static void AttachReveal(FrameworkElement element)
    {
        element.IsVisibleChanged += (_, _) =>
        {
            if (element.IsVisible) Reveal(element);
            else element.BeginAnimation(UIElement.OpacityProperty, null);
        };
    }
}
