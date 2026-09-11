using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Shop.Contracts;

internal static partial class ShopWpfTests
{
    internal static async Task<int> RunAsync(string captureDirectory, bool fundingOnly = false)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            int result = 1;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = WorkAsync(); Dispatcher.Run(); completion.TrySetResult(result);
            async Task WorkAsync()
            {
                Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                LauncherShellV2? shell = null;
                string locale = LauncherLocalization.CurrentLocale;
                BindingErrors errors = new();
                SourceLevels priorLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
                try
                {
                    Directory.CreateDirectory(captureDirectory);
                    PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
                    PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                    foreach (string resource in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WotLK.Launcher;component/" + resource, UriKind.Relative) });
                    shell = new LauncherShellV2
                    {
                        Width = 1440, Height = 860, Left = -20000, Top = -20000,
                        WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false
                    };
                    shell.PreviewGotKeyboardFocus += (_, args) => args.Handled = true;
                    shell.SourceInitialized += (_, _) =>
                    {
                        IntPtr handle = new WindowInteropHelper(shell).Handle;
                        SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
                    };
                    ShopViewV2 shop = shell.ShopPage;
                    shell.PrepareShopPreview(manualFunding:fundingOnly);
                    shell.Show(); await Pump();
                    if(fundingOnly)
                    {
                        await VerifyManualFundingAsync(shell,shop,captureDirectory);
                        Check(errors.Messages.Count==0,"No manual funding binding errors: "+string.Join("\n",errors.Messages));
                        Console.WriteLine("Manual funding WPF PASS: native create, pending reference, verified approval, dispute hold, confirmed refund, history, FR/EN, navigation and logout; no binding errors. Inactive offscreen demo only.");
                        result=0; return;
                    }
                    Check(shell.CurrentPage == LauncherShellPage.Shop, "Explicit shop preview starts without a runtime or a real account.");
                    Get<Button>(shell, "ShopNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Pump();
                    Check(shell.CurrentPage == LauncherShellPage.Shop && shop.IsVisible, "Shop opens inside the existing shell.");
                    Check(Get<ItemsControl>(shop, "ProductList").Items.Count == 4 && !shop.State.IsServiceOpen, "The catalog displays the four services before opening a product.");
                    ServiceButton(shop, 0).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Check(shop.State.IsServiceOpen && Get<ScrollViewer>(shop, "ServiceScroll").IsVisible && !Get<ScrollViewer>(shop, "PageScroll").IsVisible, "Clicking a card opens its service page.");
                    ComboBox character = Get<ComboBox>(shop, "CharacterPicker");
                    ListBox currency = Get<ListBox>(shop, "CurrencyChoices");
                    Check(character.SelectedIndex == -1, "No beneficiary is chosen implicitly.");
                    character.SelectedIndex = 0; currency.SelectedIndex = 0; await Pump();
                    Check(shop.State.SelectedCharacter?.Character.Guid == 101 && shop.State.SelectedPrice?.Price.Currency == "eur", "WPF pickers update the character and wallet.");
                    WalletBalanceV2 wallet = shell.WalletControl;
                    Check(Get<TextBlock>(wallet, "WalletAmountText").Text == "2,65 €", "Atlas credits appear in the shell header.");
                    Check(Get<TextBlock>(wallet, "EuroAmountText").Text == "10,00 €"
                        && Get<TextBlock>(wallet, "EuroAmountText").IsVisible && Get<TextBlock>(wallet, "WalletAmountText").IsVisible,
                        "Both independent wallets are visible simultaneously.");
                    Check(Get<TextBlock>(shop, "SummaryText").Text.Contains("Asteria"), "Summary reflects the selected beneficiary.");
                    Get<Button>(shell, "ShopNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Check(character.SelectedIndex == 0 && currency.SelectedIndex == 0, "Bindings preserve selection after refresh.");
                    LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
                    shell.Width = 1586; shell.Height = 992; currency.SelectedIndex = 0; await Pump();
                    Get<ScrollViewer>(shop, "PageScroll").ScrollToTop(); await Pump();
                    Capture(shell, Path.Combine(captureDirectory, "shop-reference-1586.png"));
                    Check(!shop.State.IsServiceOpen, "Refresh returns to the catalog without retaining a stale service page.");
                    Get<ScrollViewer>(shop, "PageScroll").ScrollToVerticalOffset(200); await Pump();
                    double catalogOffset = Get<ScrollViewer>(shop, "PageScroll").VerticalOffset;
                    for (int service = 1; service < 4; service++)
                    {
                        ServiceButton(shop, service).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                        Check(shop.State.IsServiceOpen && shop.State.Prices.Count == 2 && currency.IsEnabled
                            && !Get<Button>(shop, "PurchaseButton").IsEnabled, "Each service exposes both approved prices while checkout stays closed.");
                        foreach (ShopPriceRow price in shop.State.Prices)
                        {
                            currency.SelectedItem = price; await Pump();
                            var expected = ShopRuntimeTests.ApprovedPrices.Single(p => p.Id == shop.State.SelectedOffer!.Offer.Id);
                            Check(shop.State.SelectedAmount == ShopUiState.FormatEuros(price.Price.Currency == "eur" ? expected.Wallet : expected.Credits)
                                && Texts(shop).Any(t => t.Text == shop.State.SelectedCurrencyLabel), "Service recap identifies the selected currency and exact approved amount.");
                        }
                        Capture(shell, Path.Combine(captureDirectory, $"shop-service-{shop.State.SelectedOffer!.Offer.Id}.png"));
                        Get<Button>(shop, "ServiceBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                        Check(!shop.State.IsServiceOpen && Get<ScrollViewer>(shop, "PageScroll").IsVisible
                            && Math.Abs(Get<ScrollViewer>(shop, "PageScroll").VerticalOffset - catalogOffset) < 1, "Back restores the catalog and its scroll position.");
                    }
                    Get<ScrollViewer>(shop, "PageScroll").ScrollToTop(); await Pump();
                    ServiceButton(shop, 0).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Capture(shell, Path.Combine(captureDirectory, "shop-service-rename.png"));
                    HwndSource serviceHwnd = (HwndSource)PresentationSource.FromVisual(shell);
                    shell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, serviceHwnd, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Pump();
                    Check(!shop.State.IsServiceOpen, "Escape leaves the service page.");
                    currency.SelectedIndex = 0; await Pump();
                    foreach (string selectedLocale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
                    {
                        LauncherLocalization.SetLocale(selectedLocale); await Pump();
                        Check(character.SelectedIndex == 0 && currency.SelectedIndex == 0, "Language switch preserves WPF selections.");
                        Check(Texts(character).Any(t => t.Text == shop.State.SelectedCharacter!.Label), "The rendered character label updates after a language change.");
                        Check(Get<Button>(shell, "ShopNavigationButton").Content.ToString() == (LauncherLocalization.IsEnglish ? "Shop" : "Boutique"), "Shop navigation is localized.");
                        foreach ((int width, int height) in new[] { (1672, 941), (1440, 860), (1280, 760), (1080, 680) })
                        {
                            shell.Width = width; shell.Height = height; await Pump();
                            FrameworkElement navigation = Get<StackPanel>(shell, "TopNavigation");
                            FrameworkElement actions = Get<StackPanel>(shell, "TopBarActions");
                            double navigationRight = navigation.TranslatePoint(new Point(navigation.ActualWidth, 0), shell).X;
                            double actionsLeft = actions.TranslatePoint(new Point(0, 0), shell).X;
                            Check(navigationRight <= actionsLeft + 1, $"Navigation does not overlap actions at {width} ({navigationRight:F0} vs {actionsLeft:F0}).");
                            double walletRight = wallet.TranslatePoint(new Point(wallet.ActualWidth, 0), shell).X;
                            double messagesLeft = Get<Button>(shell, "MessagesNavigationButton").TranslatePoint(new Point(), shell).X;
                            Check(walletRight <= messagesLeft && messagesLeft - walletRight <= 16, "Wallet sits immediately to the left of Messages.");
                            Rect[] shopChrome = HeaderGeometry(shell);
                            Get<Button>(shell, "AddonsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                            Check(HeaderGeometry(shell).SequenceEqual(shopChrome), "Addons and Shop use exactly the same header geometry.");
                            Check(Get<TextBlock>(wallet, "EuroAmountText").IsVisible && Get<TextBlock>(wallet, "WalletAmountText").IsVisible, "Both wallets stay visible on Addons.");
                            Get<Button>(shell, "ShopNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                            Check(HeaderGeometry(shell).SequenceEqual(shopChrome), "Returning to Shop does not restyle or move the header.");
                            Check(!Get<Button>(shop, "PurchaseButton").IsEnabled, "Purchases remain closed.");
                            Check(Get<ScrollViewer>(shop, "PageScroll").ScrollableWidth == 0, "No horizontal overflow.");
                            Button firstCard = ServiceButton(shop, 0);
                            Point first = firstCard.TranslatePoint(new Point(), shop);
                            foreach (int index in Enumerable.Range(0, 4))
                            {
                                Button card = ServiceButton(shop, index);
                                Rect bounds = new(card.TranslatePoint(new Point(), shop), card.RenderSize);
                                Check(Math.Abs(bounds.Top - first.Y) < 1 && bounds.Right <= shop.ActualWidth && bounds.Bottom <= shop.ActualHeight,
                                    "All four compact service cards fit on one row in the first viewport.");
                                Check(card.ActualWidth is >= 230 and <= 350 && card.ActualHeight <= 400,
                                    "Compact cards retain readable widths without the previous oversized frames.");
                            }
                            Check(Math.Abs(shop.CardImageHeight / (firstCard.ActualWidth - 2) - 9d / 16) < .001, "Smaller artwork preserves the landscape ratio.");
                            TextBlock shopTitle = Get<TextBlock>(shop, "PageTitle");
                            TextBlock addonsTitle = Get<TextBlock>(Get<AddonsViewV2>(shell, "AddonsView"), "PageTitle");
                            Check(shopTitle.FontSize == addonsTitle.FontSize && shopTitle.FontWeight == addonsTitle.FontWeight && ReferenceEquals(shopTitle.Foreground, addonsTitle.Foreground),
                                "Shop uses exactly the same adaptive title size, weight and ice gradient as Addons.");
                            Image rateCoin = Get<Image>(shop, "ShortcutGoldCoin");
                            TextBlock rateNumber = Get<TextBlock>(shop, "ShortcutRateNumber");
                            Check(rateCoin.IsVisible && rateCoin.Source is DrawingImage && rateCoin.ActualWidth >= 14
                                && rateCoin.TranslatePoint(new Point(), shop).X > rateNumber.TranslatePoint(new Point(rateNumber.ActualWidth, 0), shop).X,
                                "A visible gold coin immediately follows the conversion rate number.");
                            foreach (int index in Enumerable.Range(0, 4))
                            {
                                ShopOfferRow offer = shop.State.Offers[index];
                                List<TextBlock> texts = Texts(ServiceButton(shop, index)).ToList();
                                Check(texts.Any(t => t.Text == offer.WalletPrice) && texts.Any(t => t.Text == offer.CreditPrice)
                                    && texts.Any(t => t.Text == offer.WalletLabel) && texts.Any(t => t.Text == offer.CreditsLabel)
                                    && texts.Any(t => t.Text == offer.Tagline), "Each card renders its sales description and both separately named wallet prices.");
                            }
                            Check(Get<TextBlock>(shop, "ShortcutCreditsLabel").IsVisible
                                && Get<TextBlock>(shop, "ShortcutCreditsLabel").Text == shop.State.CreditsLabel, "The converter result visibly names Atlas credits.");
                            ServiceButton(shop, 3).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                            Check(shop.State.PriceLabel == (LauncherLocalization.IsEnglish ? "Wallet · 20.00 €" : "Portefeuille · 20,00 €"), $"Race-change wallet pricing is localized and explicitly named: {shop.State.PriceLabel}, index {currency.SelectedIndex}.");
                            Check(currency.SelectedItem is ShopPriceRow selected && selected.Price.Currency == shop.State.SelectedPrice?.Price.Currency,
                                "The visible currency picker and recap stay on the same valid price after switching services.");
                            Check(Get<ScrollViewer>(shop, "ServiceScroll").ScrollableWidth == 0, "Service page has no horizontal overflow.");
                            Capture(shell, Path.Combine(captureDirectory, $"shop-service-{selectedLocale}-{width}.png"));
                            Get<Button>(shop, "ServiceBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                            shop.State.SelectedOffer = shop.State.Offers[0]; currency.SelectedIndex = 0; await Pump();
                            Capture(shell, Path.Combine(captureDirectory, $"shop-{selectedLocale}-{width}.png"));
                        }
                    }
                    currency.SelectedIndex = 0; await Pump();
                    Check(shop.State.SelectedCurrencyLabel == "Wallet" && shop.State.SelectedAmount == "5.00 €", "Payment uses the independent euro wallet.");
                    Get<ScrollViewer>(shop, "PageScroll").ScrollToBottom(); await Pump();
                    Capture(shell, Path.Combine(captureDirectory, "shop-en-bottom-1080.png"));
                    ShopConversionViewV2 conversion = shell.ConversionPage;
                    Get<Button>(shop, "MoreConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Check(shell.CurrentOverlay == ShellOverlayKind.None && conversion.IsVisible && !Get<ScrollViewer>(shop, "PageScroll").IsVisible,
                        "Conversion replaces the catalog inside the shop without opening a modal.");
                    Check(wallet.IsVisible && Get<StackPanel>(shell, "TopNavigation").IsHitTestVisible, "Header wallets and navigation remain available during conversion.");
                    Get<Button>(shell, "GameNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(shell.CurrentPage == LauncherShellPage.Game && !shop.State.IsConversionOpen, "Navigation leaves conversion normally.");
                    Get<Button>(shell, "MessagesNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Check(shell.CurrentPage == LauncherShellPage.Chat, "Messages remains directly accessible.");
                    Get<Button>(shell, "ShopNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Get<Button>(shop, "MoreConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    TextBox amount = Get<TextBox>(conversion, "ConversionAmount");
                    foreach ((string gold, string euros) in new[] { ("1", "0.01 €"), ("10", "0.10 €"), ("100", "1.00 €"), ("400", "4.00 €") })
                    {
                        amount.Text = gold; await Pump();
                        Check(Get<TextBlock>(conversion, "ConversionCreditText").Text == euros, $"{gold} gold renders {euros} at the agreed rate.");
                    }
                    amount.Text = "212"; await Pump();
                    Check(Get<TextBlock>(conversion, "ConversionCreditText").Text == "2.12 €", "212 gold renders EUR 2.12 in the integrated conversion view.");
                    Check(Get<Button>(conversion, "ConvertButton").IsEnabled, "Only the isolated demo permits simulated conversion.");
                    amount.SelectAll();
                    TextCompositionEventArgs letters = new(InputManager.Current.PrimaryKeyboardDevice, new TextComposition(InputManager.Current, amount, "gold")) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
                    amount.RaiseEvent(letters); Check(letters.Handled, "Letter input is blocked by the real TextBox handler.");
                    DataObjectPastingEventArgs paste = new(new DataObject(DataFormats.UnicodeText, "212 po"), false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
                    amount.RaiseEvent(paste); Check(paste.CommandCancelled, "Pasting units is blocked without reading or changing the system clipboard.");
                    TextCompositionEventArgs decimalPoint = new(InputManager.Current.PrimaryKeyboardDevice, new TextComposition(InputManager.Current, amount, ".")) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
                    amount.RaiseEvent(decimalPoint); Check(decimalPoint.Handled, "Decimal point typing is blocked.");
                    DataObjectPastingEventArgs decimalPaste = new(new DataObject(DataFormats.UnicodeText, "423,5067"), false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
                    amount.RaiseEvent(decimalPaste); Check(decimalPaste.CommandCancelled, "Fractional gold paste is blocked.");
                    ComboBox source = Get<ComboBox>(conversion, "ConversionCharacterPicker");
                    Get<Button>(conversion, "MaximumButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Check(amount.Text == "423", "Max button uses whole gold only.");
                    source.SelectedIndex = 1; await Pump();
                    Check(amount.Text == "120" && Get<TextBlock>(conversion, "MaximumGoldText").Text == "120", "Source picker clamps to the second character's maximum.");
                    amount.Text = "121"; await Pump(); Check(!Get<Button>(conversion, "ConvertButton").IsEnabled, "Overspending disables confirmation.");
                    source.SelectedIndex = 2; await Pump(); Check(!amount.IsEnabled && !Get<Button>(conversion, "MaximumButton").IsEnabled, "Online character cannot expose or spend stale gold.");
                    source.SelectedIndex = 0; amount.Text = "212"; await Pump();
                    foreach (string selectedLocale in new[] { LauncherLocalization.EnglishLocale, LauncherLocalization.FrenchLocale })
                    {
                        LauncherLocalization.SetLocale(selectedLocale); await Pump();
                        Check(Get<TextBlock>(conversion, "ConversionRateText").Text == shop.State.ConversionRate,
                            $"Rate formatting matches locale {selectedLocale}: rendered {Get<TextBlock>(conversion, "ConversionRateText").Text}, expected {shop.State.ConversionRate}, own text {LauncherLocalizationOptions.GetIsUserText(Get<TextBlock>(conversion, "ConversionRateText"))}.");
                        Check(Get<Button>(conversion, "CancelConversionButton").Content.ToString() == (LauncherLocalization.IsEnglish ? "Cancel" : "Annuler"), "Conversion button bindings retain the current locale after repeated page and language changes.");
                        foreach ((int width, int height) in new[] { (1586, 992), (1440, 860), (1280, 760), (1080, 680) })
                        {
                            shell.Width = width; shell.Height = height; await Pump();
                            Check(Get<TextBlock>(conversion, "ConversionRateText").Text == (LauncherLocalization.IsEnglish ? "100 = 1.00 €" : "100 = 1,00 €"),
                                $"Rate retains the decimal separator after resizing to {width}: {Get<TextBlock>(conversion, "ConversionRateText").Text}.");
                            Border dialog = Get<Border>(conversion, "ConversionFrame");
                            Point center = dialog.TranslatePoint(new Point(dialog.ActualWidth / 2, dialog.ActualHeight / 2), shop);
                            Check(Math.Abs(center.X - shop.ActualWidth / 2) < 2 && Math.Abs(center.Y - shop.ActualHeight / 2) < 2, "Converter remains centered within the launcher content area at every supported size.");
                            Button confirm = Get<Button>(conversion, "ConvertButton");
                            double confirmationBottom = confirm.TranslatePoint(new Point(0, confirm.ActualHeight), dialog).Y;
                            Check(dialog.ActualHeight <= shop.ActualHeight - 38 && confirmationBottom <= dialog.ActualHeight - dialog.Padding.Bottom + 1,
                                "The entire confirmation button stays within the page and outside the scrolling content.");
                            Capture(shell, Path.Combine(captureDirectory, $"shop-conversion-{selectedLocale}-{width}.png"));
                        }
                    }
                    Check(Get<TextBlock>(conversion, "ConversionCreditText").Text == "2,12 €", "French formatting follows the active locale.");
                    HwndSource hwnd = (HwndSource)PresentationSource.FromVisual(shell);
                    shell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, hwnd, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Pump();
                    Check(!conversion.IsVisible && Get<ScrollViewer>(shop, "PageScroll").IsVisible, "Escape returns to the catalog.");
                    shell.Width = 1586; shell.Height = 992;
                    Get<ScrollViewer>(shop, "PageScroll").ScrollToTop();
                    Get<Button>(shop, "MoreConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        Get<Button>(conversion, "ConvertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                        Check(conversion.IsTransferring && Get<Canvas>(conversion, "TransferLayer").Children.Count == 4, "Coins animate within the converter.");
                        Get<Button>(conversion, "CloseConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(920); await Pump();
                        Check(!conversion.IsTransferring && shop.State.CreditBalance == "2,65 €", "Leaving a pending transfer cancels it before any debit or credit.");
                        Get<Button>(shop, "MoreConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    }
                    Get<Button>(conversion, "ConvertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        Get<Button>(conversion, "ConvertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(shop.State.CreditBalance == "2,65 €" && !amount.IsEnabled, "Pending conversion blocks repeated clicks and input changes.");
                        await Task.Delay(300); Capture(shell, Path.Combine(captureDirectory, "shop-credit-animation.png"));
                        await Task.Delay(600); await Pump();
                        Check(Get<TextBlock>(wallet, "AnimatedAmountText").IsVisible && shell.FindName("ShopCreditFlightLayer") is null,
                            "Only the numeric header balance animates after the local transfer.");
                        await Task.Delay(500); await Pump();
                    }
                    Check(conversion.IsVisible && shop.State.IsConversionOpen && shop.State.HasConversionReceipt
                        && shop.State.CreditBalance == "4,77 €" && shop.State.EuroBalance == "10,00 €",
                        "Success stays in conversion with a receipt and updates only Atlas credits.");
                    Check(!Get<Button>(conversion, "ConvertButton").IsEnabled && amount.Text == "", "Success requires a fresh amount before another conversion.");
                    Check(shop.State.HasSelection && character.SelectedIndex == 0 && currency.SelectedIndex == 0,
                        "Crediting the wallet preserves product, beneficiary and payment selections.");
                    Check(Get<TextBlock>(wallet, "WalletAmountText").Text == "4,77 €" && !Get<TextBlock>(wallet, "AnimatedAmountText").IsVisible
                        && Get<Canvas>(conversion, "TransferLayer").Children.Count == 0, "Animations release their visuals and end at the credited amount.");
                    Check(Get<Button>(conversion, "CancelConversionButton").Content.ToString() == "Terminé", "Completed conversion offers Done instead of implying it can be undone.");
                    Capture(shell, Path.Combine(captureDirectory, "shop-credit-complete.png"));
                    amount.Text = "100"; await Pump();
                    Check(Get<Button>(conversion, "ConvertButton").IsEnabled && !shop.State.HasConversionReceipt
                        && Get<TextBlock>(conversion, "ConversionCreditText").Text == "1,00 €", "Another amount starts a fresh quote without leaving the converter.");
                    Get<Button>(conversion, "CloseConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Get<Button>(shell, "ShopNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Get<Button>(shop, "MoreConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Check(Get<TextBlock>(conversion, "MaximumGoldText").Text == "211", "Refresh and reopen retain the remaining gold.");
                    Get<Button>(conversion, "CloseConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        Get<Button>(shop, "MoreConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                        amount.Text = "100"; await Pump();
                        Get<Button>(conversion, "ConvertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                        shop.State.ConfigurePreview(ShopPreviewData.Create()); await shop.State.RefreshAsync();
                        await Task.Delay(920); await Pump();
                        Check(!conversion.IsTransferring && shop.State.CreditBalance == "2,65 €", "Account reset cancels pending transfer without crediting the new session.");
                    }
                    shop.State.Configure(_ => Task.FromResult(ShopRuntimeTests.Snapshot with { Characters = [] }));
                    await shop.State.RefreshAsync(); await Pump();
                    Check(!character.IsEnabled && character.SelectedItem is null, "No-character state disables beneficiary picker and clears prior selection.");
                    shop.State.Configure(_ => Task.FromResult(ShopRuntimeTests.Snapshot with { Offers = [] }));
                    await shop.State.RefreshAsync(); await Pump();
                    Check(Get<TextBlock>(shop, "StatusText").IsVisible && !Get<Button>(shop, "PurchaseButton").IsVisible, "Empty catalog shows a status instead of stale checkout.");
                    Capture(shell, Path.Combine(captureDirectory, "shop-en-empty-1080.png"));
                    shop.State.ConfigurePreview(ShopPreviewData.Create() with { CreditBalanceEuroCents = ShopSnapshot.MaximumBalanceCents, EuroBalanceCents = ShopSnapshot.MaximumBalanceCents });
                    await shop.State.RefreshAsync();
                    shell.Width = 1080; shell.Height = 680; await Pump();
                    foreach (string walletLocale in new[] { LauncherLocalization.EnglishLocale, LauncherLocalization.FrenchLocale })
                    {
                        LauncherLocalization.SetLocale(walletLocale); await Pump();
                        double walletEdge = wallet.TranslatePoint(new Point(wallet.ActualWidth, 0), shell).X;
                        foreach (string name in new[] { "WalletAmountText", "EuroAmountText" })
                        {
                            TextBlock balanceText = Get<TextBlock>(wallet, name);
                            Check(balanceText.TranslatePoint(new Point(balanceText.ActualWidth, 0), shell).X < walletEdge,
                                "Even maximum balances remain inside the compact wallet; the full value is available in its tooltip.");
                        }
                    }
                    shop.State.ConfigurePreview(ShopPreviewData.Create()); await shop.State.RefreshAsync(); shop.State.OpenConversion();
                    shell.Width = 1586; shell.Height = 992; amount.Text = "10"; await Pump();
                    Check(Get<TextBlock>(conversion, "ConversionCreditText").Text == "0,10 €", "The reported ten-gold case now displays ten cents in French.");
                    Capture(shell, Path.Combine(captureDirectory, "shop-rate-10-gold.png"));
                    await VerifyWalletNavigationAsync(shell, shop, captureDirectory);
                    await VerifyHistoryNavigationAsync(shell, shop, captureDirectory);
                    await VerifyErgonomicsAsync(shell, shop, captureDirectory);
                    await VerifyHeaderSessionAsync();
                    Check(errors.Messages.Count == 0, "No WPF binding errors: " + string.Join("\n", errors.Messages));
                    Console.WriteLine("Shop WPF PASS: four adaptive service cards, two selectable currency blocks with balances and deficits, accessible help, factual service details and known eligibility, fixed purchase summary, exact deficit funding and return to the original service/recipient/currency, explicit conversion debit and credit, consistent wallet colors, automatic refresh and error retry, transaction history and filters, FR/EN at four widths, numeric typing/paste, per-character max and overdraw, cancellable coin transfer, retained receipt, session isolation and no binding errors; offscreen inactive fixtures only.");
                    result = 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error); result = 1; }
                finally
                {
                    shell?.Close(); LauncherLocalization.SetLocale(locale);
                    PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
                    PresentationTraceSources.DataBindingSource.Switch.Level = priorLevel;
                    app.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }
        }) { IsBackground = true, Name = "AtlasShopOffscreenFixture" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromMinutes(3));
    }

    private static async Task VerifyHistoryNavigationAsync(LauncherShellV2 shell, ShopViewV2 shop, string captureDirectory)
    {
        shop.State.ConfigurePreview(ShopPreviewData.Create()); await shop.State.RefreshAsync(); await Pump();
        ShopHistoryViewV2 page = shop.HistoryPage;
        ListBox list = Get<ListBox>(page, "HistoryList");
        ComboBox kinds = Get<ComboBox>(page, "HistoryKindPicker"), wallets = Get<ComboBox>(page, "HistoryWalletPicker");
        foreach (string locale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
        {
            LauncherLocalization.SetLocale(locale); await Pump();
            foreach ((int width, int height) in new[] { (1586, 992), (1440, 860), (1280, 760), (1080, 680) })
            {
                shell.Width = width; shell.Height = height; await Pump();
                Get<Button>(shop, "ShopHistoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                kinds.SelectedIndex = 0; wallets.SelectedIndex = 0; await Pump();
                Check(page.IsVisible && shop.State.IsHistoryOpen && !shop.State.IsWalletOpen && !shop.State.IsServiceOpen && !shop.State.IsConversionOpen
                    && list.Items.Count == 3, "The catalog history link opens all preview transactions as an exclusive page.");
                Check(Descendants(list).OfType<ScrollViewer>().All(v => v.ScrollableWidth == 0), "History remains readable without horizontal scrolling.");
                Check(Texts(list).Any(t => t.Text == (LauncherLocalization.IsEnglish ? "Wallet" : "Portefeuille"))
                    && Texts(list).Any(t => t.Text == (LauncherLocalization.IsEnglish ? "Atlas credits" : "Crédits Atlas")), "Rendered history explicitly identifies both currencies.");
                Capture(shell, Path.Combine(captureDirectory, $"shop-history-{locale}-{width}.png"));
                wallets.SelectedIndex = 1; await Pump();
                Check(list.Items.Count == 2 && shop.State.FilteredHistory.All(r => r.Transaction.Currency == "eur"), "Wallet filter shows only euro-wallet operations.");
                kinds.SelectedIndex = 3; await Pump();
                Check(list.Items.Count == 1 && Texts(list).Any(t => t.Text == (LauncherLocalization.IsEnglish ? "−5.00 €" : "−5,00 €")), "Purchase filter retains the actual debit sign and amount.");
                kinds.SelectedIndex = 2; await Pump();
                Check(list.Items.Count == 0 && Get<Border>(page, "HistoryEmptyState").IsVisible && shop.State.HistoryAvailable, "A filter with no matches has its own empty state.");
                kinds.SelectedIndex = 0; wallets.SelectedIndex = 0;
                Get<Button>(page, "HistoryBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(!shop.State.IsHistoryOpen && Get<ScrollViewer>(shop, "PageScroll").IsVisible, "History back restores the catalog.");
                Get<Button>(shell.WalletControl, "EuroWalletButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Get<TextBox>(shop.WalletPage, "WalletAmountInput").Text = "12,50";
                Get<Button>(shop.WalletPage, "WalletHistoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(shop.State.IsHistoryOpen && page.IsVisible && !shop.WalletPage.IsVisible, "The wallet also opens history.");
                HwndSource hwnd = (HwndSource)PresentationSource.FromVisual(shell);
                shell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, hwnd, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Pump();
                Check(shop.State.IsWalletOpen && !shop.State.IsHistoryOpen && shop.State.WalletTopUpCents == 1250, "Escape returns to the wallet and preserves the draft amount.");
                Get<Button>(shop.WalletPage, "WalletBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            }
        }
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        Get<Button>(shell.WalletControl, "AtlasWalletButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Get<TextBox>(shop.ConversionPage, "ConversionAmount").Text = "10"; await Pump();
        Get<Button>(shop.ConversionPage, "ConvertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(1000); await Pump();
        Check(shop.State.HistoryRows.Count == 4 && shop.State.HistoryRows[0].Transaction.AmountCents == 10 && shop.State.CreditBalance == "2,75 €",
            "The actual conversion control records one completed transaction after the transfer.");
        Get<Button>(shell, "ShopNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Get<Button>(shop, "ShopHistoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(list.Items.Count == 4 && Texts(list).Any(t => t.Text == "+0,10 €"), "The new conversion remains visible after catalog refresh.");
        Capture(shell, Path.Combine(captureDirectory, "shop-history-after-conversion-1080.png"));
        Get<Button>(shell, "AddonsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(!shop.State.IsHistoryOpen, "Leaving Shop closes history.");
        Get<Button>(shell, "ShopNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        shop.State.Configure(_ => Task.FromResult(ShopRuntimeTests.Snapshot)); await shop.State.RefreshAsync();
        Get<Button>(shop, "ShopHistoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(!shop.State.HistoryAvailable && list.Items.Count == 0 && Get<TextBlock>(page, "HistoryEmptyTitle").Text.Contains("bientôt"), "Unavailable journal is shown honestly instead of fabricated records.");
        Capture(shell, Path.Combine(captureDirectory, "shop-history-unavailable-1080.png"));
        shop.State.Configure(_ => Task.FromResult(ShopRuntimeTests.Snapshot with { History = [] })); await shop.State.RefreshAsync(); await Pump();
        Check(shop.State.HistoryAvailable && Get<TextBlock>(page, "HistoryEmptyTitle").Text.Contains("Aucune opération"), "An available empty history is a different state.");
        Capture(shell, Path.Combine(captureDirectory, "shop-history-empty-1080.png"));
        shop.State.ResetSession(); await Pump();
        Check(!shop.State.IsHistoryOpen && list.Items.Count == 0 && kinds.SelectedIndex == 0 && wallets.SelectedIndex == 0, "Sign-out clears rendered history and both filters.");
    }

    private static async Task VerifyWalletNavigationAsync(LauncherShellV2 shell, ShopViewV2 shop, string captureDirectory)
    {
        ShopWalletViewV2 walletPage = shop.WalletPage;
        WalletBalanceV2 header = shell.WalletControl;
        Button credits = Get<Button>(header, "AtlasWalletButton"), euros = Get<Button>(header, "EuroWalletButton");
        TextBox amount = Get<TextBox>(walletPage, "WalletAmountInput");
        ListBox methods = Get<ListBox>(walletPage, "PaymentMethodPicker");
        shop.State.ConfigurePreview(ShopPreviewData.Create()); await shop.State.RefreshAsync();
        foreach (string locale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
        {
            LauncherLocalization.SetLocale(locale); await Pump();
            Check(Get<TextBlock>(header, "EuroKindText").Text == (LauncherLocalization.IsEnglish ? "Wallet" : "Portefeuille"), "Header label replaces Euros with the localized Wallet name.");
            foreach ((int width, int height) in new[] { (1586, 992), (1440, 860), (1280, 760), (1080, 680) })
            {
                shell.Width = width; shell.Height = height;
                Get<Button>(shell, "GameNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                credits.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(shell.CurrentPage == LauncherShellPage.Shop && shop.ConversionPage.IsVisible && shop.State.IsConversionOpen && !walletPage.IsVisible, "Clicking Atlas credits from Game opens the converter.");
                Get<Button>(shop.ConversionPage, "CloseConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Get<ScrollViewer>(shop, "PageScroll").ScrollToTop(); await Pump();
                Button shortcut = Get<Button>(shop, "MoreConversionButton");
                Rect shortcutBounds = new(shortcut.TranslatePoint(new Point(), shop), shortcut.RenderSize);
                double cardsTop = Get<ItemsControl>(shop, "ProductList").TranslatePoint(new Point(), shop).Y;
                Check(shortcutBounds.Top >= 0 && shortcutBounds.Bottom < cardsTop && shortcutBounds.Left > shop.ActualWidth / 2 && shortcutBounds.Right <= shop.ActualWidth,
                    "Compact conversion shortcut stays visible above the cards, on the right.");
                Check(shortcut.ActualWidth <= 280 && shortcut.ActualHeight <= 76, "The conversion shortcut retains its compact size.");
                shortcut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(shop.State.IsConversionOpen, "The compact shortcut still opens conversion.");
                Get<Button>(shell, "AddonsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                euros.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(shell.CurrentPage == LauncherShellPage.Shop && walletPage.IsVisible && shop.State.IsWalletOpen && !shop.State.IsConversionOpen && !shop.State.IsServiceOpen,
                    "Clicking the euro wallet from Addons opens the independent funding page.");
                amount.Text = LauncherLocalization.IsEnglish ? "12.50" : "12,50"; await Pump();
                for (int method = 0; method < 3; method++)
                {
                    methods.SelectedIndex = method; await Pump();
                    Check(Get<TextBlock>(walletPage, "WalletSelectedMethod").Text == shop.State.PaymentMethods[method].Name, "Each payment method updates the draft summary.");
                }
                Check(Get<TextBlock>(walletPage, "WalletDraftAmount").Text == (LauncherLocalization.IsEnglish ? "12.50 €" : "12,50 €")
                    && Get<TextBlock>(walletPage, "WalletAfterAmount").Text == (LauncherLocalization.IsEnglish ? "22.50 €" : "22,50 €"), "The funding summary uses exact cents and the active locale.");
                Check(!Get<Button>(walletPage, "WalletPayButton").IsEnabled && shop.State.EuroBalance == (LauncherLocalization.IsEnglish ? "10.00 €" : "10,00 €"),
                    "Payment remains closed and selecting a method never changes the current wallet.");
                Check(Get<ScrollViewer>(walletPage, "WalletScroll").ScrollableWidth == 0 && Descendants(methods).OfType<ScrollViewer>().All(v => v.ScrollableWidth == 0), "Funding page and payment cards do not overflow horizontally.");
                Get<ScrollViewer>(walletPage, "WalletScroll").ScrollToTop(); await Pump();
                Button payButton = Get<Button>(walletPage, "WalletPayButton");
                Check(payButton.TranslatePoint(new Point(0, payButton.ActualHeight), shop).Y <= shop.ActualHeight, "Payment continuation stays within the first viewport at every supported size.");
                Capture(shell, Path.Combine(captureDirectory, $"shop-wallet-{locale}-{width}.png"));
                Get<Button>(walletPage, "WalletTenButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(amount.Text == "10" && shop.State.WalletTopUpCents == 1000, "Wallet amount presets update the bound amount.");
                Get<Button>(walletPage, "WalletBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                Check(!shop.State.IsWalletOpen && Get<ScrollViewer>(shop, "PageScroll").IsVisible, "Wallet back returns to the catalog.");
                euros.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                HwndSource hwnd = (HwndSource)PresentationSource.FromVisual(shell);
                shell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, hwnd, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await Pump();
                Check(!shop.State.IsWalletOpen, "Escape closes the wallet page.");
            }
        }
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        if (SystemParameters.ClientAreaAnimation)
        {
            credits.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            Get<TextBox>(shop.ConversionPage, "ConversionAmount").Text = "10"; await Pump();
            Get<Button>(shop.ConversionPage, "ConvertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            Check(shop.ConversionPage.IsTransferring, "The preview transfer starts before navigating to Wallet.");
            euros.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(920); await Pump();
            Check(!shop.ConversionPage.IsTransferring && shop.State.IsWalletOpen && shop.State.CreditBalance == "2,65 €" && shop.State.EuroBalance == "10,00 €",
                "Wallet navigation cancels pending conversion without debiting gold or crediting either wallet.");
        }
        shop.State.ResetSession(); await Pump();
        Check(!shop.State.IsWalletOpen && amount.Text == "" && methods.SelectedItem is null, "Session reset clears the actual wallet form and selection.");
    }

    private static async Task VerifyHeaderSessionAsync()
    {
        ShellUiState identity = new() { IsAuthenticated = false, Username = "Fixture" };
        LauncherShellV2 fixture = new(identity, LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready), LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateProfile(ProfilePreviewScenario.SignedIn), SettingsUiState.Empty)
        { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        // No Show(): this fixture exercises shell bindings without creating a native window or runtime.
        fixture.PreviewGotKeyboardFocus += (_, e) => e.Handled = true;
        try
        {
            int reads = 0;
            TaskCompletionSource<ShopSnapshot> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.AttachShop(_ => ++reads == 2 ? late.Task : Task.FromResult(ShopRuntimeTests.Snapshot with { CreditBalanceEuroCents = reads * 100, EuroBalanceCents = 800 }));
            Get<Button>(fixture.WalletControl, "AtlasWalletButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Get<Button>(fixture.WalletControl, "EuroWalletButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Get<Button>(fixture.ShopPage, "ShopHistoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Get<Button>(fixture.ShopPage, "ServiceFundingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!fixture.ShopPage.State.IsHistoryOpen && fixture.ShopPage.State.HistoryRows.Count == 0,
                "Signed-out history access cannot reveal transactions or open the journal.");
            Check(reads == 0 && !fixture.ShopPage.State.IsWalletOpen && !fixture.ShopPage.State.IsConversionOpen, "Signed-out wallet shortcuts cannot read balances or open funding pages.");
            AuthSessionSnapshot signedIn = AuthSessionSnapshot.Initial with { State = LauncherSessionState.Authenticated, Username = "Fixture" };
            AuthSessionSnapshot signedOut = signedIn with { State = LauncherSessionState.SignedOut };
            identity.ApplySessionSnapshot(signedIn); await Pump();
            Check(reads == 1 && fixture.ShopPage.State.CreditBalance == "1,00 €", "Initial authentication refreshes the header before visiting the shop.");
            identity.ApplySessionSnapshot(signedIn with { Sequence = 2 }); await Pump();
            Check(reads == 1, "Unchanged authenticated session notifications do not repeatedly request the shop.");
            identity.ApplySessionSnapshot(signedOut); await Pump();
            Check(fixture.ShopPage.State.CreditBalance == "—" && fixture.ShopPage.State.EuroBalance == "—", "Signing out clears both visible wallets.");
            identity.ApplySessionSnapshot(signedIn); await Pump();
            Check(reads == 2 && fixture.ShopPage.State.IsLoading, "Reauthentication initiates a fresh header read.");
            identity.ApplySessionSnapshot(signedOut);
            identity.ApplySessionSnapshot(signedIn with { Username = "OtherFixture" });
            late.SetResult(ShopRuntimeTests.Snapshot with { CreditBalanceEuroCents = 999 }); await Pump(); await Pump();
            Check(reads == 3 && fixture.ShopPage.State.CreditBalance == "3,00 €" && !fixture.ShopPage.State.IsConversionPreview,
                "Account change during an outstanding header read discards the old balance and loads the new account.");
            // This fixture has no AuthCommands runtime to close the login overlay on success.
            fixture.AuthState.IsOpen = false; await Pump();
            Get<Button>(fixture, "AddonsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Get<Button>(fixture.WalletControl, "EuroWalletButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            Check(reads == 4 && fixture.ShopPage.State.IsWalletOpen, "Entering Wallet from another page automatically refreshes the live account snapshot.");
            Get<Button>(fixture, "AddonsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Get<Button>(fixture.WalletControl, "AtlasWalletButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            Check(reads == 5 && fixture.ShopPage.State.IsConversionOpen, "Entering conversion from another page also refreshes the live account snapshot.");
        }
        finally { fixture.Close(); }
    }

    private static Button ServiceButton(ShopViewV2 shop, int index)
    {
        ItemsControl products = Get<ItemsControl>(shop, "ProductList");
        DependencyObject container = products.ItemContainerGenerator.ContainerFromIndex(index) ?? throw new InvalidOperationException("Service card container missing.");
        return Descendants(container).OfType<Button>().Single();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static Rect[] HeaderGeometry(LauncherShellV2 shell) => new[] { "TitleBar", "BrandIdentity", "BrandLogo", "TopNavigation", "TopBarActions", "WalletHeader" }
        .Select(name => Get<FrameworkElement>(shell, name)).Select(view => new Rect(view.TranslatePoint(new Point(), shell), view.RenderSize)).ToArray();

    private static Task Pump() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static T Get<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static IEnumerable<TextBlock> Texts(DependencyObject root)
    {
        if (root is TextBlock text) yield return text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (TextBlock child in Texts(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Capture(FrameworkElement view, string path)
    {
        view.UpdateLayout();
        RenderTargetBitmap bitmap = new((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view); PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path); encoder.Save(file);
    }
    private sealed class BindingErrors : TraceListener
    {
        internal List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
}
