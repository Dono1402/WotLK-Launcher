using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    private static readonly Duration SceneDuration = new(TimeSpan.FromMilliseconds(300));
    private static readonly DependencyProperty SceneProgressProperty = DependencyProperty.Register(
        "SceneProgress", typeof(double), typeof(LauncherShellV2), new PropertyMetadata(0d));
    private int _serviceTransitionGeneration;
    private bool _animateNextNavigation;
    private bool _isServiceTransitioning;
    internal bool IsServiceTransitioning => _isServiceTransitioning;
    internal bool? ServiceAnimationsOverride { get; set; }
    private bool AnimateServices => IsLoaded && (ServiceAnimationsOverride ?? SystemParameters.ClientAreaAnimation);

    private FrameworkElement[] ScenePages => [GameView, MinecraftView, ShopView, AddonsView,
        PatchNotesView, SettingsView, AccountView, ChatView, ArmoryView];
    private FrameworkElement[] SceneBackdrops => [SecondaryBackdrop, AtlasBackdrop, WotlkGameBackdrop, MinecraftBackdrop, ShopBackdrop];

    private FrameworkElement ScenePage => CurrentPage switch
    {
        LauncherShellPage.Shop => ShopView,
        LauncherShellPage.Addons => AddonsView,
        LauncherShellPage.PatchNotes => PatchNotesView,
        LauncherShellPage.Settings => SettingsView,
        LauncherShellPage.Account => AccountView,
        LauncherShellPage.Chat => ChatView,
        LauncherShellPage.Armory => ArmoryView,
        _ => ShellState.IsWotlkSelected ? GameView : MinecraftView
    };

    private FrameworkElement SceneBackdrop => CurrentPage switch
    {
        LauncherShellPage.Game => ShellState.IsWotlkSelected ? WotlkGameBackdrop : MinecraftBackdrop,
        LauncherShellPage.Shop when !_isAtlasWalletPage => ShopBackdrop,
        _ => ShellState.IsWotlkSelected ? SecondaryBackdrop : AtlasBackdrop
    };

    // Keep live native visuals. A retarget starts from the current animated values;
    // no software screenshot, stale command snapshot or offscreen strip is involved.
    private void PresentScene(bool animate)
    {
        FrameworkElement target = ScenePage;
        FrameworkElement background = SceneBackdrop;
        int generation = ++_serviceTransitionGeneration;
        BeginAnimation(SceneProgressProperty, null);
        foreach (FrameworkElement element in ScenePages.Concat(SceneBackdrops))
        {
            double value = element.Visibility == Visibility.Visible ? element.Opacity : 0;
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = value;
        }
        if (!animate || !AnimateServices)
        {
            FinishServiceTransition();
            return;
        }
        _isServiceTransitioning = true;

        // Native WebView2 is an HWND surface and cannot participate in a WPF opacity fade.
        // Hide it before exposing another page rather than trying to photograph it.
        if (!ReferenceEquals(target, ArmoryView)) ArmoryView.Visibility = Visibility.Collapsed;
        bool outgoing = ScenePages.Any(page => !ReferenceEquals(page, target)
            && page.Visibility == Visibility.Visible && page.Opacity > .001);
        foreach (FrameworkElement page in ScenePages)
        {
            bool incoming = ReferenceEquals(page, target);
            if (ReferenceEquals(page, ArmoryView))
            {
                // Reveal native browser content only once its new context is settled.
                page.Visibility = Visibility.Collapsed;
                page.IsEnabled = page.IsHitTestVisible = false;
                continue;
            }
            if (!incoming && (page.Visibility != Visibility.Visible || page.Opacity <= .001))
            {
                page.Visibility = Visibility.Collapsed;
                page.IsEnabled = page.IsHitTestVisible = false;
                continue;
            }
            bool unchanged = incoming && !outgoing && page.Opacity >= .999;
            page.IsEnabled = page.IsHitTestVisible = unchanged;
            if (page.IsKeyboardFocusWithin && !unchanged && IsActive) ServiceSelector.Focus();
            page.Visibility = Visibility.Visible;
            if (unchanged) continue;
            double from = page.Opacity;
            page.Opacity = from;
            var fade = new DoubleAnimation(from, incoming ? 1 : 0,
                TimeSpan.FromMilliseconds(incoming ? 150 : 75))
            {
                BeginTime = incoming && outgoing ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            page.BeginAnimation(OpacityProperty, fade);
        }

        // Blend the incoming opaque background over the current composition. On
        // reversal keep the lower layer and fade its covering layers away.
        if (background.Opacity <= .001 || background.Visibility != Visibility.Visible)
            Panel.SetZIndex(background, SceneBackdrops.Max(Panel.GetZIndex) + 1);
        background.Visibility = Visibility.Visible;
        int targetZ = Panel.GetZIndex(background);
        foreach (FrameworkElement backdrop in SceneBackdrops)
        {
            if (backdrop.Visibility != Visibility.Visible) continue;
            double to = ReferenceEquals(backdrop, background) ? 1
                : Panel.GetZIndex(backdrop) >= targetZ ? 0 : backdrop.Opacity;
            backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(backdrop.Opacity, to, SceneDuration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        }

        var completion = new DoubleAnimation(0, 1, SceneDuration);
        completion.Completed += (_, _) =>
        {
            if (generation == _serviceTransitionGeneration) FinishServiceTransition();
        };
        BeginAnimation(SceneProgressProperty, completion);
    }

    private void FinishServiceTransition()
    {
        ++_serviceTransitionGeneration;
        _isServiceTransitioning = false;
        BeginAnimation(SceneProgressProperty, null);
        FrameworkElement target = ScenePage;
        FrameworkElement background = SceneBackdrop;
        foreach (FrameworkElement page in ScenePages)
        {
            bool active = ReferenceEquals(page, target);
            page.BeginAnimation(OpacityProperty, null);
            page.Opacity = active ? 1 : 0;
            page.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            page.IsEnabled = page.IsHitTestVisible = active;
        }
        foreach (FrameworkElement backdrop in SceneBackdrops)
        {
            bool active = ReferenceEquals(backdrop, background);
            backdrop.BeginAnimation(OpacityProperty, null);
            backdrop.Opacity = active ? 1 : 0;
            backdrop.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            Panel.SetZIndex(backdrop, 0);
        }
        foreach (Button tab in new[] { AddonsNavigationButton, ShopNavigationButton })
            tab.IsHitTestVisible = tab.Focusable = ShellState.IsWotlkSelected;
        UpdateHeaderNavigationSpacing();
    }
}
