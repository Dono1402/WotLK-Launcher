using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

internal static partial class ShellNavigationWpfTests
{
    private static async Task ValidateServicesAsync(LauncherShellV2 shell, string output)
    {
        ComboBox selector = (ComboBox)shell.FindName("ServiceSelector");
        FrameworkElement game = Element("GameView"), minecraft = Element("MinecraftView");
        object gameState = shell.GameState, accountState = shell.AccountState;
        Check(shell.ResizeMode == ResizeMode.CanMinimize && Math.Abs(shell.Width - 1597.6) < 1
            && Math.Abs(shell.Height - 996.8) < 1, "The launcher retains its fixed dimensions and cannot be resized.");
        Directory.CreateDirectory(output);
        try
        {
            foreach (string locale in new[] { "fr-FR", "en-US" })
            {
                LauncherLocalization.SetLocale(locale);
                selector.SelectedValue = LauncherService.Wotlk;
                await Settle();
                Check(shell.ShellState.GameName == "WOTLK Server" && game.IsVisible && !minecraft.IsVisible,
                    "The default WotLK workspace keeps its real game view.");
                ValidateHeader();
                if (locale == "fr-FR") Capture("wotlk");

                Invoke((Button)Element("AddonsNavigationButton"), pointer: true);
                shell.FriendsState.IsOpen = true;
                selector.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
                selector.SelectedValue = LauncherService.Minecraft;
                await Settle();
                Check(shell.ShellState.SelectedService == LauncherService.Minecraft && minecraft.IsVisible && !game.IsVisible,
                    "The real ComboBox switches to a separate Minecraft workspace.");
                Check(shell.CurrentPage == LauncherShellPage.Game && shell.CurrentOverlay == ShellOverlayKind.None,
                    "Switching from Addons closes stale panels and returns to the new game's home.");
                Check(!Element("AddonsNavigationButton").IsVisible && !Element("ShopNavigationButton").IsVisible
                    && !Element("WalletHeader").IsVisible, "Minecraft cannot expose the WotLK shop or addons.");
                Check(ReferenceEquals(gameState, shell.GameState) && ReferenceEquals(accountState, shell.AccountState)
                    && shell.ShellState.Username == "AtlasFixture", "Switching preserves the same game operation and account state.");
                ValidateHeader();
                if (locale == "fr-FR") Capture("minecraft");

                foreach (string button in new[] { "SettingsButton", "MessagesNavigationButton", "PatchNotesNavigationButton" })
                {
                    Invoke((Button)Element(button), pointer: true);
                    Check(shell.ShellState.SelectedService == LauncherService.Minecraft, "Shared pages keep the service selection.");
                }
                Invoke((Button)Element("GameNavigationButton"), pointer: true);
                Check(minecraft.IsVisible, "The Game tab returns to Minecraft after using shared pages.");

                foreach (bool authentication in new[] { true, false })
                {
                    if (authentication) shell.AuthState.IsOpen = true;
                    else shell.AvatarCropState.IsOpen = true;
                    await Settle();
                    selector.SelectedValue = LauncherService.Wotlk;
                    Check(shell.ShellState.SelectedService == LauncherService.Minecraft
                        && Equals(selector.SelectedValue, LauncherService.Minecraft), "A modal blocks a service change and restores the selection.");
                    shell.AuthState.IsOpen = shell.AvatarCropState.IsOpen = false;
                    await Settle();
                }
                // A WotLK deep link must change the context as well as the visible page.
                Invoke((Button)Element("AddonsNavigationButton"), pointer: false);
                Check(shell.ShellState.SelectedService == LauncherService.Wotlk && shell.CurrentPage == LauncherShellPage.Addons,
                    "A WotLK deep link restores its service before opening the target page.");
                Invoke((Button)Element("GameNavigationButton"), pointer: false);
                await Settle();
                Check(game.IsVisible && !minecraft.IsVisible && Element("ShopNavigationButton").IsVisible,
                    "Returning to WotLK restores its navigation and game view.");
            }
            Check(!shell.IsActive && !shell.IsKeyboardFocusWithin && !shell.ShowInTaskbar && shell.Left < -10000,
                "The fixture never takes focus or opens the installed launcher.");
        }
        finally { LauncherLocalization.SetLocale("fr-FR"); }

        FrameworkElement Element(string name) => (FrameworkElement)shell.FindName(name);
        async Task Settle() { await Task.Delay(300); shell.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
        void ValidateHeader()
        {
            Rect previous = Rect.Empty;
            foreach (string name in new[] { "BrandIdentity", "ServiceSelector", "TopNavigation", "TopBarActions" })
            {
                FrameworkElement element = Element(name);
                Rect bounds = new(element.TranslatePoint(new Point(), shell), element.RenderSize);
                Check(bounds.Left >= 0 && bounds.Right <= shell.ActualWidth && (previous.IsEmpty || previous.Right <= bounds.Left + 1),
                    $"Header blocks do not overlap at {shell.Width}: {name} {bounds}, previous {previous}.");
                previous = bounds;
            }
            foreach (string name in new[] { "ServiceSelector", "GameNavigationButton", "SettingsButton", "FriendsButton", "CloseWindowButton" })
            {
                FrameworkElement element = Element(name);
                Point point = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), shell);
                Check(IsWithin(shell.InputHitTest(point) as DependencyObject, element), $"{name} is clickable at {shell.Width}.");
            }
        }
        void Capture(string name)
        {
            if (Math.Abs(shell.Width - 1598) > 1 && Math.Abs(shell.Width - 1080) > 1) return;
            RenderTargetBitmap bitmap = new((int)shell.ActualWidth, (int)shell.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(shell);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream file = File.Create(Path.Combine(output, $"{name}-{shell.Width:0}.png"));
            encoder.Save(file);
        }
    }
}
