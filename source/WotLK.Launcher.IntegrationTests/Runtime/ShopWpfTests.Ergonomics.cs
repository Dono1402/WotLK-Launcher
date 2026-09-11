using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ShopWpfTests
{
    private static async Task VerifyErgonomicsAsync(LauncherShellV2 shell, ShopViewV2 shop, string captureDirectory)
    {
        ShopSnapshot preview = ShopPreviewData.Create();
        ShopSnapshot richSource = preview with { Characters = preview.Characters.Select(c => c.Guid == 101 ? c with { GoldCopper = 9_000_000 } : c).ToArray() };
        ListBox choices = Get<ListBox>(shop, "CurrencyChoices");
        ComboBox beneficiary = Get<ComboBox>(shop, "CharacterPicker");
        Button funding = Get<Button>(shop, "ServiceFundingButton");
        ShopWalletViewV2 wallet = shop.WalletPage;
        ShopConversionViewV2 conversion = shop.ConversionPage;
        ScrollViewer serviceScroll = Get<ScrollViewer>(shop, "ServiceScroll");

        foreach (string locale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
        {
            LauncherLocalization.SetLocale(locale); await Pump();
            foreach ((int width, int height) in new[] { (1586, 992), (1440, 860), (1280, 760), (1080, 680) })
            {
                shell.Width = width; shell.Height = height;
                shop.State.ConfigurePreview(richSource); await shop.State.RefreshAsync(); await Pump();
                Check(shop.FindName("RefreshButton") is null && !Get<Button>(shop, "RetryButton").IsVisible,
                    "A healthy catalog has no isolated refresh action or error retry.");
                Check(!Texts(Get<ScrollViewer>(shop, "PageScroll")).Any(t => t.Text is "ATLAS LAUNCHER" or "WRATH OF THE LICH KING"),
                    "The catalog no longer repeats the launcher brand or realm on its cards.");
                foreach (int index in Enumerable.Range(0, 4))
                    Check(Texts(ServiceButton(shop, index)).Single(t => t.Text == shop.State.Offers[index].Tagline).FontSize >= 13,
                        "Compact descriptions gain readable text size without shrinking cards.");

                await OpenService(0, 2, 1); // Online beneficiary, credits, distinct offline funding source.
                Check(choices.Items.Count == 2 && choices.IsVisible && choices.SelectedItem is ShopPriceRow { IsCredits: true },
                    $"Both currency choices are visible and selectable: {choices.Items.Count} rows, visible {choices.IsVisible}, index {choices.SelectedIndex}.");
                foreach (ShopPriceRow row in choices.Items)
                {
                    ListBoxItem container = (ListBoxItem)choices.ItemContainerGenerator.ContainerFromItem(row);
                    Check(container.Focusable && container.IsTabStop, "Currency items participate in native keyboard navigation.");
                    Check(AutomationProperties.GetName(container).Contains(row.Amount) && Texts(container).Any(t => t.Text == row.AvailableLabel)
                        && Texts(container).Any(t => t.Text == row.BalanceStatus), "Each currency choice exposes price, available balance and deficit to sighted and assistive users.");
                }
                Check(Get<TextBlock>(shop, "EligibilityTitleText").Text == (LauncherLocalization.IsEnglish ? "Character is online" : "Personnage en ligne"),
                    "Service eligibility shows the actual selected character's prerequisite.");
                Check(Texts(serviceScroll).Any(t => t.Text == shop.State.IncludedLabel) && Texts(serviceScroll).Any(t => t.Text == shop.State.PreservedLabel),
                    "Service information separates included content, preserved elements and conditions.");
                Check(Get<Button>(shop, "ShopHistoryButton").Height >= 34, "History has a readable, larger click target.");
                AssertVisibleInside(Get<Border>(shop, "ServiceFooter"), shop);
                AssertVisibleInside(Get<Button>(shop, "PurchaseButton"), shop);
                serviceScroll.ScrollToBottom(); await Pump();
                AssertVisibleInside(Get<Border>(shop, "ServiceFooter"), shop);
                Check(Get<ScrollViewer>(shop, "ServiceScroll").ScrollableWidth == 0 && funding.IsEnabled, "The service remains usable without horizontal scrolling.");
                Capture(shell, Path.Combine(captureDirectory, $"shop-funding-service-{locale}-{width}.png"));
                funding.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(conversion.IsVisible && shop.State.ConversionGold == "435" && shop.State.SelectedConversionCharacter?.Character.Guid == 101
                    && shop.State.SelectedCharacter?.Character.Guid == 303 && shop.State.HasFundingReturn,
                    "The service action prefills the exact credits deficit on an offline source without changing the beneficiary.");
                Check(Get<Button>(conversion, "ConvertButton").Content.ToString() == (LauncherLocalization.IsEnglish ? "Convert 435 gold" : "Convertir 435 po")
                    && Get<TextBlock>(conversion, "ConversionReceiveText").Text.Contains(LauncherLocalization.IsEnglish ? "4.35 €" : "4,35 €"),
                    "The visible conversion action names the exact debit and resulting credits.");
                Check(Descendants(conversion).OfType<Button>().Count(b => b.Tag?.ToString() == "100") == 1,
                    "Only one Max action remains, with no duplicate 100-percent button.");
                AssertVisibleInside(Get<Button>(conversion, "ConvertButton"), shop);
                Capture(shell, Path.Combine(captureDirectory, $"shop-funding-conversion-{locale}-{width}.png"));
                Get<Button>(conversion, "CloseConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(shop.State.IsServiceOpen && choices.SelectedIndex == 1 && beneficiary.SelectedIndex == 2
                    && shop.State.CreditBalance == (LauncherLocalization.IsEnglish ? "2.65 €" : "2,65 €"), "Returning without converting preserves the selection and all balances.");

                Get<Button>(shop, "ServiceBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                await OpenService(1, 2, 0);
                funding.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(wallet.IsVisible && Get<TextBox>(wallet, "WalletAmountInput").Text == "50"
                    && Get<TextBlock>(wallet, "WalletFundingContext").Text.Contains("Elune"), "Wallet opens with only the 50-euro deficit and the original service context.");
                foreach (string name in new[] { "WalletCurrentAmount", "WalletDraftAmount", "WalletAfterAmount" })
                    Check(((SolidColorBrush)Get<TextBlock>(wallet, name).Foreground).Color == Color.FromRgb(169, 220, 250), "Every euro-wallet amount uses the same blue currency color.");
                Check(!Get<Button>(wallet, "WalletPayButton").IsEnabled, "No funding draft can initiate a real payment.");
                Capture(shell, Path.Combine(captureDirectory, $"shop-funding-wallet-{locale}-{width}.png"));
                Get<Button>(wallet, "WalletHistoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Get<Button>(shop.HistoryPage, "HistoryBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(wallet.IsVisible && shop.State.HasFundingReturn && shop.State.WalletAmount == "50", "History returns to the same funding draft.");
                Escape(); await Pump();
                Check(shop.State.IsServiceOpen && shop.State.SelectedOffer?.Offer.Id == "character-level-70"
                    && beneficiary.SelectedIndex == 2 && choices.SelectedIndex == 0 && !shop.State.HasFundingReturn,
                    "Escape from the wallet restores the exact service, recipient and currency.");
                Get<Button>(shop, "ServiceBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            }
        }

        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        shop.State.ConfigurePreview(richSource); await shop.State.RefreshAsync(); await Pump();
        await OpenService(0, 2, 1); funding.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Get<Button>(conversion, "ConvertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(1000); await Pump();
        Check(shop.State.CreditBalance == "7,00 €" && shop.State.IsConversionOpen && shop.State.HistoryRows.Count == 4 && shop.State.HasConversionReceipt,
            "A real preview control transfer reaches the needed balance exactly once and retains its receipt.");
        Get<Button>(conversion, "CancelConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(shop.State.IsServiceOpen && choices.SelectedIndex == 1 && beneficiary.SelectedIndex == 2 && !funding.IsVisible
            && Texts(choices).Any(t => t.Text == "Disponible : 7,00 €"), "After funding, the same selected currency shows its refreshed balance and no longer offers unnecessary funding.");
        serviceScroll.ScrollToTop(); await Pump();
        // Wait for the independent header counter so delivery captures show the final balance.
        await Task.Delay(550); await Pump();
        Capture(shell, Path.Combine(captureDirectory, "shop-service-after-funding-1080.png"));
        Expander help = Get<Expander>(shop, "ServiceWalletHelp"); help.IsExpanded = true; help.BringIntoView(); await Pump();
        Check(Texts(help).Any(t => t.IsVisible && t.Text == shop.State.WalletHelpDescription), "The optional currency help expands into readable text.");
        AssertVisibleInside(Get<Border>(shop, "ServiceFooter"), shop);
        Capture(shell, Path.Combine(captureDirectory, "shop-service-help-1080.png")); help.IsExpanded = false;
        Get<Button>(shop, "ServiceBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        await OpenService(1, 0, 0);
        Check(!funding.IsEnabled && Get<TextBlock>(shop, "EligibilityTitleText").Text == "Niveau 70 déjà atteint", "A level 80 character is not invited to fund a level 70 boost.");
        Capture(shell, Path.Combine(captureDirectory, "shop-service-level-restriction-1080.png"));

        shop.State.Configure(_ => Task.FromException<ShopSnapshot>(new HttpRequestException("fixture", null, HttpStatusCode.ServiceUnavailable)));
        Get<Button>(shell, "ShopNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(Get<Button>(shop, "RetryButton").IsVisible && Get<Button>(shop, "RetryButton").IsEnabled, "A failed automatic load offers a visible retry.");
        Capture(shell, Path.Combine(captureDirectory, "shop-retry-1080.png"));
        shop.State.Configure(_ => Task.FromResult(ShopRuntimeTests.Snapshot));
        Get<Button>(shop, "RetryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(shop.State.Offers.Count == 4 && !Get<Button>(shop, "RetryButton").IsVisible, "Retry recovers the catalog and disappears afterwards.");
        await OpenService(0, 0, 0);
        Check(Texts(choices).Count(t => t.Text == "Solde indisponible") == 2 && !funding.IsVisible,
            "Both unknown live balances are shown explicitly, without invented top-up amounts.");
        Capture(shell, Path.Combine(captureDirectory, "shop-service-unavailable-balances-1080.png"));
        shop.State.ResetSession(); await Pump();
        Check(choices.Items.Count == 0 && !shop.State.HasFundingReturn && !Get<Border>(shop, "ServiceFooter").IsVisible, "Sign-out removes currency choices, funding context and the service summary.");

        async Task OpenService(int service, int character, int currency)
        {
            ServiceButton(shop, service).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            beneficiary.SelectedIndex = character; choices.SelectedIndex = currency; await Pump();
        }
        void Escape()
        {
            HwndSource hwnd = (HwndSource)PresentationSource.FromVisual(shell);
            shell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, hwnd, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
    }

    private static void AssertVisibleInside(FrameworkElement element, FrameworkElement viewport)
    {
        Rect bounds = new(element.TranslatePoint(new Point(), viewport), element.RenderSize);
        Check(element.IsVisible && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1,
            $"{element.Name} stays within the usable viewport ({bounds} in {viewport.RenderSize}).");
    }
}
