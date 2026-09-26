using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    private int _serviceTransitionGeneration;
    private void ServiceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ServiceSelector.SelectedValue is not LauncherService service) return;
        if (!SelectService(service))
            ServiceSelector.SetCurrentValue(Selector.SelectedValueProperty, ShellState.SelectedService);
    }

    internal bool SelectService(LauncherService service)
    {
        if (!Enum.IsDefined(service) || IsAuthenticationRequired || !_overlayCoordinator.CanNavigate
            || !ShellState.IsNavigationEnabled) return false;
        if (ShellState.SelectedService == service) return true;

        BitmapSource? previousWorkspace = CaptureServiceWorkspace();
        FinishServiceTransition();
        // Keep the same account and running operations; only the visible workspace changes.
        ShellState.SelectService(service);
        NavigateTo(LauncherShellPage.Game);
        ApplyAdaptiveLayout();
        StartServiceTransition(previousWorkspace, service);
        return true;
    }

    private void ApplyServiceChrome()
    {
        bool wotlk = ShellState.IsWotlkSelected;
        AddonsNavigationButton.Visibility = ShopNavigationButton.Visibility =
            wotlk ? Visibility.Visible : Visibility.Collapsed;
        WalletHeader.Visibility = wotlk ? Visibility.Visible : Visibility.Collapsed;
        MinecraftBackdrop.Visibility = wotlk ? Visibility.Collapsed : Visibility.Visible;
    }

    private BitmapSource? CaptureServiceWorkspace()
    {
        if (!SystemParameters.ClientAreaAnimation || !IsLoaded || CurrentPage == LauncherShellPage.Armory
            || WorkspaceContent.ActualWidth <= 0 || WorkspaceContent.ActualHeight <= 0) return null;

        // Snapshot only WPF workspace visuals, never the title bar, popups or account overlays.
        // WebView2 cannot be captured this way, so Armory uses the incoming fade instead.
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(new Rect(WorkspaceContent.RenderSize)));
            foreach (FrameworkElement element in new FrameworkElement[] { SecondaryBackdrop, MinecraftBackdrop,
                ShopBackdrop, GameView, ShopView, AddonsView, PatchNotesView, SettingsView, AccountView, ChatView,
                MinecraftView, WorkspaceTransitionLayer })
            {
                if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
                Point origin = element.TranslatePoint(new Point(), WorkspaceContent);
                drawing.PushOpacity(element.Opacity);
                drawing.DrawRectangle(new VisualBrush(element), null, new Rect(origin, element.RenderSize));
                drawing.Pop();
            }
            drawing.Pop();
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(WorkspaceContent);
        RenderTargetBitmap snapshot = new((int)Math.Ceiling(WorkspaceContent.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(WorkspaceContent.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        snapshot.Render(visual);
        snapshot.Freeze();
        return snapshot;
    }

    private void StartServiceTransition(BitmapSource? previous, LauncherService service)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        int generation = _serviceTransitionGeneration;
        if (previous is null)
        {
            FrameworkElement incoming = ShellState.IsWotlkSelected ? GameView : MinecraftView;
            incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
                { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            return;
        }
        WorkspaceTransitionLayer.Source = previous;
        WorkspaceTransitionLayer.Visibility = Visibility.Visible;
        DoubleAnimation fade = new(1, 0, TimeSpan.FromMilliseconds(320))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) => { if (generation == _serviceTransitionGeneration) FinishServiceTransition(); };
        WorkspaceTransitionLayer.BeginAnimation(OpacityProperty, fade);
        WorkspaceTransitionOffset.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, service == LauncherService.Minecraft ? -8 : 8, fade.Duration)
            { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void FinishServiceTransition()
    {
        _serviceTransitionGeneration++;
        WorkspaceTransitionLayer.BeginAnimation(OpacityProperty, null);
        WorkspaceTransitionOffset.BeginAnimation(TranslateTransform.XProperty, null);
        WorkspaceTransitionLayer.Visibility = Visibility.Collapsed;
        WorkspaceTransitionLayer.Source = null;
        GameView.BeginAnimation(OpacityProperty, null);
        MinecraftView.BeginAnimation(OpacityProperty, null);
    }
}
