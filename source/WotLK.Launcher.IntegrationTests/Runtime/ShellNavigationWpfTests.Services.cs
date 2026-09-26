using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static partial class ShellNavigationWpfTests
{
    private static async Task ValidateServicesAsync(LauncherShellV2 shell, string output)
    {
        ComboBox selector = (ComboBox)shell.FindName("ServiceSelector");
        FrameworkElement game = Element("GameView"), minecraft = Element("MinecraftView");
        object gameState = shell.GameState, accountState = shell.AccountState;
        shell.ShopPage.State.ConfigurePreview(ShopPreviewData.Create() with { CreditBalanceEuroCents = 108700, EuroBalanceCents = 0 });
        await shell.ShopPage.State.RefreshAsync();
        Image transition = (Image)Element("WorkspaceTransitionLayer");
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
                Check(shell.WalletControl.Width == 264
                    && Grid.GetRow((Button)shell.WalletControl.FindName("EuroWalletButton")) == 0
                    && Grid.GetColumn((Button)shell.WalletControl.FindName("EuroWalletButton")) == 2
                    && double.IsPositiveInfinity(((TextBlock)shell.WalletControl.FindName("WalletAmountText")).MaxWidth),
                    "The wallet keeps its original side-by-side presentation without the compact amount limit.");
                TextBlock amount = (TextBlock)shell.WalletControl.FindName("WalletAmountText");
                FormattedText measuredAmount = new(amount.Text, System.Globalization.CultureInfo.CurrentCulture,
                    amount.FlowDirection, new Typeface(amount.FontFamily, amount.FontStyle, amount.FontWeight, amount.FontStretch),
                    amount.FontSize, amount.Foreground, VisualTreeHelper.GetDpi(amount).PixelsPerDip);
                Check(amount.Text != "—" && measuredAmount.Width <= amount.ActualWidth,
                    "The representative 1087.00 credit balance fits without ellipsis.");
                if (locale == "fr-FR") Capture("wotlk");
                if (locale == "fr-FR") await ValidateServiceMenu();

                Invoke((Button)Element("AddonsNavigationButton"), pointer: true);
                shell.FriendsState.IsOpen = true;
                selector.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
                selector.SelectedValue = LauncherService.Minecraft;
                if (SystemParameters.ClientAreaAnimation)
                {
                    Check(transition.IsVisible && transition.Source is not null && !transition.IsHitTestVisible,
                        "Switching starts a workspace crossfade without blocking input.");
                    await Task.Delay(130);
                    Check(transition.Opacity > 0 && transition.Opacity < 1, "The old workspace fades over time rather than cutting instantly.");
                }
                await Settle();
                Check(!transition.IsVisible && transition.Source is null, "The transition releases its snapshot after completion.");
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

                selector.SelectedValue = LauncherService.Minecraft;
                selector.SelectedValue = LauncherService.Wotlk;
                selector.SelectedValue = LauncherService.Minecraft;
                await Settle();
                Check(minecraft.IsVisible && !game.IsVisible && transition.Source is null,
                    "Rapid switches leave the final requested service visible with no stale animation.");
                selector.SelectedValue = LauncherService.Wotlk;
                Invoke((Button)Element("SettingsButton"), pointer: true);
                Check(shell.CurrentPage == LauncherShellPage.Settings && transition.Source is null,
                    "Navigating during a transition immediately clears the old workspace overlay.");
                Invoke((Button)Element("GameNavigationButton"), pointer: true);
            }
            Check(!shell.IsActive && !shell.IsKeyboardFocusWithin && !shell.ShowInTaskbar && shell.Left < -10000,
                "The fixture never takes focus or opens the installed launcher.");
        }
        finally { LauncherLocalization.SetLocale("fr-FR"); }

        FrameworkElement Element(string name) => (FrameworkElement)shell.FindName(name);
        async Task Settle() { await Task.Delay(400); shell.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
        async Task ValidateServiceMenu()
        {
            selector.ApplyTemplate();
            Popup popup = (Popup)selector.Template.FindName("PART_Popup", selector);
            // Keep the native popup closed. Exercise its template animations and
            // render the detached menu visual without showing a desktop window.
            popup.SetValue(Popup.IsOpenProperty, false);
            try
            {
                selector.IsDropDownOpen = true;
                await Settle();
                Check(!popup.IsOpen, "The popup remains closed during this offscreen template check.");
                RotateTransform chevron = (RotateTransform)selector.Template.FindName("ServiceChevron", selector);
                Check(Math.Abs(chevron.Angle - 180) < 1, "The chevron animates into its open state.");
                Border menu = (Border)popup.Child;
                menu.Measure(new Size(274, double.PositiveInfinity));
                menu.Arrange(new Rect(menu.DesiredSize));
                menu.UpdateLayout();
                RenderTargetBitmap bitmap = new((int)Math.Ceiling(menu.ActualWidth + 24), (int)Math.Ceiling(menu.ActualHeight + 24), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(menu);
                PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream file = File.Create(Path.Combine(output, "service-menu.png"))) encoder.Save(file);
                selector.IsDropDownOpen = false;
                await Settle();
                Check(Math.Abs(chevron.Angle) < 1, "The chevron returns to its closed state.");
            }
            finally { selector.IsDropDownOpen = false; popup.ClearValue(Popup.IsOpenProperty); }
        }
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
            if (name == "wotlk")
            {
                FrameworkElement header = Element("TitleBar");
                RenderTargetBitmap headerBitmap = new((int)Math.Ceiling(header.ActualWidth), (int)Math.Ceiling(header.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                headerBitmap.Render(header);
                PngBitmapEncoder headerEncoder = new(); headerEncoder.Frames.Add(BitmapFrame.Create(headerBitmap));
                using FileStream headerFile = File.Create(Path.Combine(output, "header.png"));
                headerEncoder.Save(headerFile);
            }
        }
    }
}
