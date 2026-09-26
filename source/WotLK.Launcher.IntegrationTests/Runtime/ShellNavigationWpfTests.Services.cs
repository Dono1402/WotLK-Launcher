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
using WotLK.Launcher.UI.V2.Views;

internal static partial class ShellNavigationWpfTests
{
    private static async Task ValidateServicesAsync(LauncherShellV2 shell, string output)
    {
        ComboBox selector = (ComboBox)shell.FindName("ServiceSelector");
        FrameworkElement game = Element("GameView"), minecraft = Element("MinecraftView");
        object gameState = shell.GameState, accountState = shell.AccountState;
        shell.ShopPage.State.ConfigurePreview(ShopPreviewData.Create() with { CreditBalanceEuroCents = 108700, EuroBalanceCents = 0 });
        await shell.ShopPage.State.RefreshAsync();
        shell.ServiceAnimationsOverride = true;
        Check(shell.ResizeMode == ResizeMode.CanMinimize && Math.Abs(shell.Width - 1597.6) < 1
            && Math.Abs(shell.Height - 996.8) < 1, "The launcher retains its fixed dimensions and cannot be resized.");
        Directory.CreateDirectory(output);
        try
        {
            foreach (string locale in new[] { "fr-FR", "en-US" })
            {
                LauncherLocalization.SetLocale(locale);
                selector.SetCurrentValue(Selector.SelectedValueProperty, LauncherService.Wotlk);
                await Settle();
                FrameworkElement updateButton = Element("LauncherUpdateButton");
                updateButton.Visibility = Visibility.Collapsed;
                await Settle();
                Check(shell.ShellState.GameName == "WOTLK Server" && game.IsVisible && !minecraft.IsVisible,
                    "The default WotLK workspace keeps its real game view.");
                ValidateHeader();
                Check(((Button)Element("GameNavigationButton")).Padding.Left == 23,
                    "The idle header uses the original generous tab spacing instead of reserving room for hidden indicators.");
                // Keep the pre-selector visual scale, even when the local badge
                // and the widest operation indicators share the fixed header.
                Check(Element("TitleBar").Height == 80 && Element("TitleBar").Margin == new Thickness(22, 22, 22, 0)
                    && Element("BrandLogo").Width == 42 && ((TextBlock)Element("BrandName")).FontSize == 20
                    && ((Button)Element("GameNavigationButton")).FontSize == 16
                    && Element("SettingsButton").Width == 44 && Element("ProfileAvatarVisual").Width == 42,
                    "The service selector preserves the original header height, margins, typography and icon scale.");
                FrameworkElement activityButton = Element("ActivityButton");
                try
                {
                    updateButton.Visibility = Visibility.Visible;
                    activityButton.Width = 72;
                    activityButton.Visibility = Visibility.Visible;
                    await Settle();
                    ValidateHeader();
                    Check(((Button)Element("GameNavigationButton")).Padding.Left < 23,
                        "Tab spacing yields only when the extra update and progress controls occupy the header.");
                }
                finally
                {
                    activityButton.ClearValue(FrameworkElement.WidthProperty);
                    activityButton.ClearValue(UIElement.VisibilityProperty);
                    updateButton.Visibility = Visibility.Collapsed;
                    await Settle();
                }
                Check(((Button)Element("GameNavigationButton")).Padding.Left == 23,
                    "The tabs automatically regain their original spacing after the indicators disappear.");
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
                await ValidateServiceMenu("wotlk-" + locale);

                Invoke((Button)Element("AddonsNavigationButton"), pointer: true);
                shell.FriendsState.IsOpen = true;
                selector.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
                selector.SetCurrentValue(Selector.SelectedValueProperty, LauncherService.Minecraft);
                Check(shell.IsServiceTransitioning && !minecraft.IsHitTestVisible && !minecraft.IsEnabled,
                    "The incoming workspace cannot execute commands while it is still concealed.");
                await Task.Delay(160);
                Check(Element("MinecraftBackdrop").Opacity > 0 && Element("MinecraftBackdrop").Opacity < 1,
                    "The native full-window background blends over time.");
                Check(Element("AddonsView").Opacity == 0, "The previous content is gone before the next title appears.");
                await Settle();
                Check(!shell.IsServiceTransitioning && minecraft.IsEnabled, "Only the final workspace regains interaction after the transition.");
                Check(shell.ShellState.SelectedService == LauncherService.Minecraft && minecraft.IsVisible && !game.IsVisible,
                    "The real ComboBox switches to a separate Minecraft workspace.");
                Check(shell.CurrentPage == LauncherShellPage.Game && shell.CurrentOverlay == ShellOverlayKind.None,
                    "Switching from Addons closes stale panels and returns to the new game's home.");
                Check(!Element("AddonsNavigationButton").IsVisible && !Element("ShopNavigationButton").IsVisible
                    && Element("WalletHeader").IsVisible, "Minecraft hides WotLK shop and addons while retaining the shared Atlas wallet.");
                Check(ReferenceEquals(gameState, shell.GameState) && ReferenceEquals(accountState, shell.AccountState)
                    && shell.ShellState.Username == "AtlasFixture", "Switching preserves the same game operation and account state.");
                ValidateHeader();
                if (locale == "fr-FR") Capture("minecraft");
                else Capture("minecraft-en");
                await ValidateServiceMenu("minecraft-" + locale);
                var minecraftGame = (GameViewV2)minecraft;
                Button minecraftPlay = (Button)minecraftGame.FindName("PrimaryActionButton");
                Check(!minecraftPlay.IsEnabled && !minecraftGame.State!.PrimaryActionCommand.CanExecute(null)
                    && !ReferenceEquals(minecraftGame.State, shell.GameState),
                    "Minecraft cannot execute a WotLK launch or download through the shared game view.");
                Check(minecraftGame.DashboardState!.Current.RealmState == WotLK.Launcher.Dashboard.DashboardRealmState.Unknown
                    && minecraftGame.DashboardState.Current.OnlinePlayersText == "—"
                    && minecraftGame.DashboardState.Current.GatewayLatencyText == "—",
                    "Minecraft never presents WotLK telemetry or mockup numbers as live server data.");
                foreach (string textName in new[] { "HeroTitle", "HeroTitleSecond", "HeroMottoText" })
                    Check(((TextBlock)minecraftGame.FindName(textName)).GetBindingExpression(TextBlock.TextProperty) is not null,
                        "Minecraft copy remains bound WPF text after localization: " + textName);
                Button minecraftNotes = (Button)minecraftGame.FindName("LatestPatchNoteAction");
                Invoke(minecraftNotes, pointer: true);
                Check(shell.CurrentOverlay == ShellOverlayKind.PatchNote
                    && ReferenceEquals(shell.PatchNoteReaderOverlay.State, shell.DashboardState),
                    "The reused notes button opens the existing launcher release notes.");
                Invoke((Button)shell.PatchNoteReaderOverlay.FindName("CloseButton"), pointer: false);
                await Settle();

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
                    selector.SetCurrentValue(Selector.SelectedValueProperty, LauncherService.Wotlk);
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
                Check(Equals(selector.SelectedValue, LauncherService.Wotlk),
                    "The service selector follows a WotLK deep link while retaining its binding.");
                if (locale == "fr-FR") Capture("wotlk-restored");

                selector.SetCurrentValue(Selector.SelectedValueProperty, LauncherService.Minecraft);
                selector.SetCurrentValue(Selector.SelectedValueProperty, LauncherService.Wotlk);
                selector.SetCurrentValue(Selector.SelectedValueProperty, LauncherService.Minecraft);
                await Settle();
                Check(minecraft.IsVisible && !game.IsVisible && !shell.IsServiceTransitioning,
                    "Rapid switches leave the final requested service visible with no stale animation.");
                selector.SetCurrentValue(Selector.SelectedValueProperty, LauncherService.Wotlk);
                Invoke((Button)Element("SettingsButton"), pointer: true);
                Check(shell.CurrentPage == LauncherShellPage.Settings && shell.IsServiceTransitioning,
                    "Navigation retargets an active transition instead of abruptly cancelling it.");
                await Settle();
                Check(Element("SettingsView").IsVisible && Element("SettingsView").IsEnabled && !game.IsVisible && !minecraft.IsVisible,
                    "The retargeted transition finishes on the requested shared page.");
                Invoke((Button)Element("GameNavigationButton"), pointer: true);
            }
            await ValidateTransitionExperience();
            Check(!shell.IsActive && !shell.IsKeyboardFocusWithin && !shell.ShowInTaskbar && shell.Left < -10000,
                "The fixture never takes focus or opens the installed launcher.");
        }
        finally
        {
            Element("LauncherUpdateButton").ClearValue(UIElement.VisibilityProperty);
            LauncherLocalization.SetLocale("fr-FR");
        }

        FrameworkElement Element(string name) => (FrameworkElement)shell.FindName(name);
        async Task Settle() { await Task.Delay(400); shell.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
        async Task ValidateTransitionExperience()
        {
            LauncherLocalization.SetLocale("fr-FR");
            Invoke((Button)Element("GameNavigationButton"), pointer: false);
            await Settle();
            var samples = new List<object>();
            foreach (LauncherService service in new[] { LauncherService.Minecraft, LauncherService.Wotlk })
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Check(shell.SelectService(service), "A service transition is accepted.");
                double dispatchMs = timer.Elapsed.TotalMilliseconds;
                var incoming = service == LauncherService.Minecraft ? minecraft : game;
                var outgoing = service == LauncherService.Minecraft ? game : minecraft;
                var backdrop = Element(service == LauncherService.Minecraft ? "MinecraftBackdrop" : "WotlkGameBackdrop");
                Check(!incoming.IsEnabled && !incoming.IsHitTestVisible && !outgoing.IsEnabled,
                    "Both game action regions are disabled until the incoming workspace is fully presented.");
                Check(!Element("AddonsNavigationButton").IsHitTestVisible && !Element("ShopNavigationButton").IsHitTestVisible,
                    "Context tabs cannot receive clicks while they are entering or leaving the header.");
                Check(backdrop.TranslatePoint(new Point(), shell).Y == 0 && Math.Abs(backdrop.ActualHeight - shell.ActualHeight) < 1,
                    "The animated backdrop covers the entire fixed window, including behind the toolbar.");
                for (int frame = 0; frame < 6; frame++)
                {
                    await Task.Delay(40);
                    Check(incoming.Opacity <= .001 || outgoing.Opacity <= .001,
                        "Outgoing and incoming game text never overlap during the transition.");
                    samples.Add(new { service = service.ToString(), elapsedMs = timer.Elapsed.TotalMilliseconds, dispatchMs,
                        incoming = incoming.Opacity, outgoing = outgoing.Opacity, background = backdrop.Opacity,
                        interaction = incoming.IsEnabled });
                    if (frame == 4) Capture(service == LauncherService.Minecraft ? "towards-minecraft" : "towards-wotlk");
                }
                await Settle();
                Check(incoming.IsEnabled && incoming.IsVisible && !outgoing.IsVisible,
                    "The final workspace is interactive and the previous one is hidden.");
            }
            File.WriteAllText(Path.Combine(output, "transition-samples.json"),
                System.Text.Json.JsonSerializer.Serialize(samples, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // Reverse from a partially blended scene, then reverse again before it settles.
            shell.SelectService(LauncherService.Minecraft);
            await Task.Delay(140);
            shell.SelectService(LauncherService.Wotlk);
            await Task.Delay(35);
            shell.SelectService(LauncherService.Minecraft);
            await Settle();
            Check(minecraft.IsVisible && minecraft.Opacity == 1 && !game.IsVisible && !shell.IsServiceTransitioning,
                "A rapid reversal settles on the last selection without stale content or disabled controls.");

            // Global Atlas pages preserve their context across game selection.
            Invoke((Button)Element("SettingsButton"), pointer: false);
            var settings = (SettingsViewV2)Element("SettingsView");
            settings.SelectCategory(SettingsCategory.Game);
            Check(!settings.IsWotlkContext && settings.SelectedCategory == SettingsCategory.General
                && !((FrameworkElement)settings.FindName("GameCategoryButton")).IsVisible,
                "Minecraft cannot expose WotLK game settings, even via a category deep link.");
            settings.SelectCategory(SettingsCategory.Diagnostic);
            Check(!((FrameworkElement)settings.FindName("WotlkClientDiagnostic")).IsVisible,
                "Minecraft does not display the WotLK client's diagnostic version.");
            Capture("minecraft-settings");
            shell.SelectService(LauncherService.Wotlk);
            await Settle();
            Check(shell.CurrentPage == LauncherShellPage.Settings && settings.SelectedCategory == SettingsCategory.Diagnostic
                && ((FrameworkElement)settings.FindName("WotlkClientDiagnostic")).IsVisible,
                "Switching services retains the shared settings page and restores the correct diagnostic context.");
            shell.SelectService(LauncherService.Minecraft);
            await Settle();
            Invoke((Button)shell.WalletControl.FindName("EuroWalletButton"), pointer: false);
            await Settle();
            Check(shell.ShellState.SelectedService == LauncherService.Minecraft && shell.ShopPage.State.IsWalletOpen
                && !((FrameworkElement)shell.ShopPage.FindName("PageScroll")).IsVisible,
                "The Atlas wallet opens under Minecraft without exposing the WotLK catalog or switching games.");
            Capture("minecraft-wallet");
            shell.ShopPage.State.CloseWallet();
            await Settle();
            Check(shell.CurrentPage == LauncherShellPage.Settings && shell.ShellState.SelectedService == LauncherService.Minecraft,
                "Closing the global wallet returns to the previous page without switching games.");
            Invoke((Button)shell.WalletControl.FindName("AtlasWalletButton"), pointer: false);
            await Settle();
            Check(shell.ShellState.SelectedService == LauncherService.Minecraft && shell.ShopPage.State.IsConversionOpen
                && shell.ShopPage.State.ConversionSubtitle.Contains("WotLK"),
                "Atlas credit conversion explicitly identifies its WotLK source without changing the selected game.");
            shell.ShopPage.State.CloseConversion();
            await Settle();
            var profile = (ProfileMenuV2)Element("ProfileMenu");
            Invoke((Button)profile.FindName("ManageProfileButton"), pointer: false);
            await Settle();
            Check(shell.ShellState.SelectedService == LauncherService.Minecraft && shell.CurrentPage == LauncherShellPage.Account
                && ((AccountViewV2)Element("AccountView")).SelectedSection == AccountSection.Profile,
                "The Minecraft profile action opens the real Atlas profile editor instead of WotLK or account security.");
            Check(((FrameworkElement)((AccountViewV2)Element("AccountView")).FindName("ProfileTabButton")).IsVisible,
                "The shared profile editor provides a visible route back from security and sessions.");
            Capture("minecraft-profile");

            // Each game remembers its last game-specific page during this session.
            shell.SelectService(LauncherService.Wotlk);
            await Settle();
            Invoke((Button)Element("AddonsNavigationButton"), pointer: false);
            shell.SelectService(LauncherService.Minecraft);
            await Settle();
            shell.SelectService(LauncherService.Wotlk);
            await Settle();
            Check(shell.CurrentPage == LauncherShellPage.Addons, "Returning to WotLK restores its last game-specific page.");
            Invoke((Button)Element("GameNavigationButton"), pointer: false);
            shell.ServiceAnimationsOverride = false;
            shell.SelectService(LauncherService.Minecraft);
            Check(!shell.IsServiceTransitioning && minecraft.IsVisible && minecraft.IsEnabled && !game.IsVisible
                && !Element("AddonsNavigationButton").IsVisible,
                "Reduced motion presents the final content and context tabs immediately.");
            shell.SelectService(LauncherService.Wotlk);
            shell.ServiceAnimationsOverride = true;
            await Settle();
            Capture("wotlk-final");
        }
        async Task ValidateServiceMenu(string variant)
        {
            selector.ApplyTemplate();
            Popup popup = (Popup)selector.Template.FindName("PART_Popup", selector);
            // Keep the native popup closed. Exercise its template animations and
            // render the detached menu visual without showing a desktop window.
            System.Windows.Data.BindingOperations.SetBinding(popup, Popup.IsOpenProperty,
                new System.Windows.Data.Binding { Source = false, Mode = System.Windows.Data.BindingMode.OneWay });
            try
            {
                selector.IsDropDownOpen = true;
                await Settle();
                Check(!popup.IsOpen, "The popup remains closed during this offscreen template check.");
                RotateTransform chevron = (RotateTransform)selector.Template.FindName("ServiceChevron", selector);
                Check(Math.Abs(chevron.Angle - 180) < 1, "The chevron animates into its open state.");
                Border menu = (Border)popup.Child;
                menu.Measure(new Size(menu.Width + menu.Margin.Left + menu.Margin.Right, double.PositiveInfinity));
                menu.Arrange(new Rect(menu.DesiredSize));
                menu.UpdateLayout();
                RenderTargetBitmap bitmap = new((int)Math.Ceiling(menu.ActualWidth + 24), (int)Math.Ceiling(menu.ActualHeight + 24), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(menu);
                PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream file = File.Create(Path.Combine(output, "service-menu-" + variant + ".png"))) encoder.Save(file);
                selector.IsDropDownOpen = false;
                if (SystemParameters.ClientAreaAnimation)
                    Check(ServiceSelectorMotion.GetIsPopupVisible(selector) && !menu.IsHitTestVisible,
                        "The closing selector menu stays visible for its exit fade but no longer accepts input.");
                await Settle();
                Check(Math.Abs(chevron.Angle) < 1, "The chevron returns to its closed state.");
                Check(!ServiceSelectorMotion.GetIsPopupVisible(selector), "The selector releases its popup after the closing animation.");
            }
            finally { selector.IsDropDownOpen = false; popup.ClearValue(Popup.IsOpenProperty); }
        }
        void ValidateHeader()
        {
            FrameworkElement header = Element("TitleBar");
            Rect previous = Rect.Empty;
            foreach (string name in new[] { "BrandIdentity", "ServiceSelector", "TopNavigation", "TopBarActions" })
            {
                FrameworkElement element = Element(name);
                Rect bounds = new(element.TranslatePoint(new Point(), header), element.RenderSize);
                Check(bounds.Left >= 0 && bounds.Right <= header.ActualWidth && (previous.IsEmpty || previous.Right <= bounds.Left + 1),
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
                DrawingVisual headerVisual = new();
                using (DrawingContext drawing = headerVisual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(header), null, new Rect(header.RenderSize));
                headerBitmap.Render(headerVisual);
                PngBitmapEncoder headerEncoder = new(); headerEncoder.Frames.Add(BitmapFrame.Create(headerBitmap));
                using FileStream headerFile = File.Create(Path.Combine(output, "header.png"));
                headerEncoder.Save(headerFile);
            }
        }
    }
}
