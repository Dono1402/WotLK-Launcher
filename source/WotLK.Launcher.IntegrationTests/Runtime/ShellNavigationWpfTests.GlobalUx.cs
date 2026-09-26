using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ShellNavigationWpfTests
{
    private static async Task ValidateGlobalUxAsync(LauncherShellV2 shell, string output)
    {
        FrameworkElement Element(string name) => (FrameworkElement)shell.FindName(name);
        void Call(object target, string name, params object?[] args) => target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
        async Task Settle() { await Task.Delay(350); shell.UpdateLayout(); }
        void Capture(string name)
        {
            RenderTargetBitmap image = new((int)Math.Ceiling(shell.ActualWidth), (int)Math.Ceiling(shell.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(shell);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image));
            using FileStream file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }

        shell.ServiceAnimationsOverride = true;
        shell.SelectService(LauncherService.Minecraft);
        await Settle();
        Call(shell, "ActivityCenter_NavigationRequested", null,
            new ActivityNavigationRequestedEventArgs(ActivityNavigationTarget.Game, "wotlk", LauncherService.Wotlk));
        Check(shell.ShellState.IsWotlkSelected && shell.CurrentPage == LauncherShellPage.Game,
            "A WotLK game activity opened from Minecraft routes to WotLK, not the selected game's home.");
        await Settle();
        Invoke((Button)Element("SettingsButton"), pointer: false);
        Check(shell.IsServiceTransitioning && !Element("SettingsView").IsEnabled,
            "Normal page navigation has a transition and does not expose incoming actions early.");
        await Settle();
        Check(Element("AtlasBackdrop").Visibility == Visibility.Visible && Element("SecondaryBackdrop").Visibility != Visibility.Visible,
            "Global Atlas pages use the same neutral backdrop under WotLK.");
        shell.SelectService(LauncherService.Minecraft);
        await Settle();
        Check(Element("AtlasBackdrop").Visibility == Visibility.Visible,
            "Switching universes keeps the global Atlas page and backdrop.");

        var settings = (SettingsViewV2)Element("SettingsView");
        shell.SettingsState.ApplyRuntimeView(LauncherV2PreviewData.CreateSettings(SettingsPreviewScenario.General).Current with { IsRuntimeConnected = true });
        settings.SelectCategory(SettingsCategory.General);
        await Settle(); Capture("atlas-settings-fr");

        Invoke((Button)shell.WalletControl.FindName("EuroWalletButton"), pointer: false);
        await Settle();
        ComboBox selector = (ComboBox)Element("ServiceSelector");
        selector.ApplyTemplate();
        Popup popup = (Popup)selector.Template.FindName("PART_Popup", selector);
        BindingOperations.SetBinding(popup, Popup.IsOpenProperty, new Binding { Source = false, Mode = BindingMode.OneWay });
        try
        {
            selector.IsDropDownOpen = true;
            await Settle();
            HwndSource source = (HwndSource)PresentationSource.FromVisual(shell)!;
            KeyEventArgs key = new(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            shell.RaiseEvent(key);
            Check(key.Handled && !selector.IsDropDownOpen && shell.ShopPage.State.IsWalletOpen,
                "Escape dismisses the service menu before the wallet underneath it.");
        }
        finally { selector.IsDropDownOpen = false; BindingOperations.ClearBinding(popup, Popup.IsOpenProperty); }

        ArmoryViewV2 armory = (ArmoryViewV2)Element("ArmoryView");
        // Stop before any native browser, cache source or account API can be opened.
        armory.Configure(_ => Task.FromResult<uint?>(null), shell.AccountState,
            () => throw new InvalidOperationException("Synthetic profile: no external source."));
        FriendUiItem friend = shell.FriendsState.Current.Friends.First();
        Call(shell, "FriendsDrawer_PublicProfileRequested", null, new FriendPublicProfileRequestedEventArgs(friend.AccountId, friend.Username));
        await Settle();
        Check(shell.ShellState.SelectedService == LauncherService.Minecraft && shell.CurrentPage == LauncherShellPage.Armory,
            "Opening an Atlas friend's profile does not silently change the selected universe.");
        Check(Element("TitleBar").IsVisible && Grid.GetRow(armory) == 1,
            "Profile pages retain the same visible global navigation, including when loading fails.");
        Invoke((Button)armory.FindName("ProfileBackButton"), pointer: false);
        await Settle();
        Check(shell.CurrentPage == LauncherShellPage.Shop && shell.ShopPage.State.IsWalletOpen
            && shell.ShellState.SelectedService == LauncherService.Minecraft && shell.FriendsState.IsOpen,
            "Returning from a friend restores the original universe, wallet subpage and friends panel.");
        shell.FriendsState.IsOpen = false;
        await Settle();

        ShopWalletViewV2 wallet = shell.ShopPage.WalletPage;
        var row = new ShopTopUpRow(new ShopTopUp(new string('a', 32), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 500, "pending"));
        var copy = new Button { DataContext = row };
        string? copied = null;
        wallet.CopyReference = value => copied = value;
        Call(wallet, "CopyReference_Click", copy, new RoutedEventArgs());
        Check(copied == row.Reference && shell.ShopPage.State.WalletActionFeedback == "Référence copiée.",
            "Copying a reference confirms the result without touching the system clipboard in the fixture.");
        wallet.CopyReference = _ => throw new COMException("Synthetic clipboard failure.");
        Call(wallet, "CopyReference_Click", copy, new RoutedEventArgs());
        Check(shell.ShopPage.State.WalletActionFeedback.Contains("Réessaie"), "Clipboard failure is visible and explains how to retry.");
        await Settle(); Capture("atlas-wallet-feedback-fr");

        shell.ShopPage.State.CloseWallet();
        await Settle();
        shell.SelectService(LauncherService.Wotlk);
        await Settle();
        Invoke((Button)Element("AddonsNavigationButton"), pointer: false);
        await Settle();
        Check(shell.CurrentPage == LauncherShellPage.Addons, "The addon return scenario starts on the visible Addons page.");
        shell.AddonsState.ApplyRuntimeView(AddonsPreviewData.Create(AddonsPreviewScenario.Default).Current with { IsRuntimeConnected = true });
        string addonId = shell.AddonsState.Current.Catalog.First().Id;
        Check(shell.AddonsState.OpenDetails(addonId), "The addon origin has a real detail panel to restore.");
        Call(shell, "FriendsDrawer_PublicProfileRequested", null, new FriendPublicProfileRequestedEventArgs(friend.AccountId, friend.Username));
        await Settle();
        Invoke((Button)armory.FindName("ProfileBackButton"), pointer: false);
        await Settle();
        Check(shell.CurrentPage == LauncherShellPage.Addons && shell.AddonsState.Current.IsDetailOpen
            && shell.AddonsState.Current.SelectedAddon?.Id == addonId,
            "Back restores the selected addon details, not only the Addons page.");
        shell.FriendsState.IsOpen = false;
        Invoke((Button)Element("ShopNavigationButton"), pointer: false);
        await Settle();
        var offer = shell.ShopPage.State.Offers.First();
        shell.ShopPage.State.OpenService(offer);
        Call(shell, "FriendsDrawer_PublicProfileRequested", null, new FriendPublicProfileRequestedEventArgs(friend.AccountId, friend.Username));
        await Settle();
        Invoke((Button)armory.FindName("ProfileBackButton"), pointer: false);
        await Settle();
        Check(shell.CurrentPage == LauncherShellPage.Shop && shell.ShopPage.State.IsServiceOpen
            && shell.ShopPage.State.SelectedOffer?.Offer.Id == offer.Offer.Id,
            "Back restores the shop service being consulted without starting a purchase.");
        shell.FriendsState.IsOpen = false;
        shell.SelectService(LauncherService.Minecraft);
        await Settle();
        shell.ServiceAnimationsOverride = null;
        AtlasMotion.EnabledOverride = false;
        try
        {
            Invoke((Button)Element("PatchNotesNavigationButton"), pointer: false);
            Check(!shell.IsServiceTransitioning && Element("PatchNotesView").IsEnabled,
                "The shared reduced-motion policy disables ordinary page transitions immediately.");
            shell.ProfileState.IsOpen = true;
            var profile = (ProfileMenuV2)Element("ProfileMenu");
            FrameworkElement panel = (FrameworkElement)profile.FindName("MenuPanel");
            Check(panel.Opacity == 1 && !panel.HasAnimatedProperties,
                "The profile panel uses the same reduced-motion policy without an active fade.");
            await Settle();
            Check(((Button)profile.FindName("QuitAtlasButton")).IsVisible, "Quit Atlas is discoverable in the existing profile menu.");
            Capture("atlas-profile-menu-fr");
            shell.ProfileState.IsOpen = false;
        }
        finally { AtlasMotion.EnabledOverride = null; shell.ServiceAnimationsOverride = true; }

        foreach (string locale in new[] { "fr-FR", "en-US" })
        {
            LauncherLocalization.SetLocale(locale);
            foreach (string name in new[] { "SettingsButton", "PatchNotesNavigationButton" })
            {
                Invoke((Button)Element(name), pointer: false);
                if (name == "SettingsButton") settings.SelectCategory(SettingsCategory.General);
                await Settle(); Capture(name + "-" + locale);
            }
            Invoke((Button)shell.WalletControl.FindName("EuroWalletButton"), pointer: false);
            await Settle(); Capture("wallet-" + locale);
            Check(((TextBlock)settings.FindName("PageTitle")).FontSize == ((TextBlock)wallet.FindName("WalletTitle")).FontSize,
                "Settings and wallet use a common secondary-page title scale at the fixed window size.");
            shell.ShopPage.State.CloseWallet(); await Settle();
            Invoke((Button)shell.WalletControl.FindName("AtlasWalletButton"), pointer: false);
            await Settle(); Capture("conversion-" + locale);
            shell.ShopPage.State.CloseConversion(); await Settle();
        }
        LauncherLocalization.SetLocale("fr-FR");
        Call(shell, "PresentAuthenticationSurface", true);
        Check(Element("LauncherSurface").Visibility == Visibility.Hidden && !Element("LauncherSurface").IsEnabled,
            "Sign-out immediately removes authenticated content and actions during the visual handoff.");
        Call(shell, "PresentAuthenticationSurface", false);
        await Settle();
        Check(Element("LauncherSurface").Opacity == 1 && Element("LauncherSurface").IsEnabled
            && Element("LoginBackdrop").Visibility == Visibility.Collapsed,
            "The sign-in handoff finishes with the application visible and no login background left over.");
        shell.SelectService(LauncherService.Wotlk);
        Invoke((Button)Element("GameNavigationButton"), pointer: false);
        await Settle(); Capture("wotlk-final-global-ux");
    }
}
