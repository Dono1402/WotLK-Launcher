using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ShopWpfTests
{
    private static async Task VerifyLocalFixesAsync(LauncherShellV2 shell, ShopViewV2 shop, string captures)
    {
        ShopSnapshot snapshot = ShopRuntimeTests.Snapshot with
        {
            CheckoutAvailable = true, EuroBalanceCents = 2000, CreditBalanceEuroCents = 100108,
            Offers = ShopServiceCatalog.CreateOffers(accountServices: true),
            Characters = [ShopRuntimeTests.Snapshot.Characters[0] with { Name = "Flowmage", Level = 35, GoldCopper = 1023130067 }],
            Purchases = new(true, [], AccountServices: true), Conversions = new(true, [])
        };
        TaskCompletionSource<ShopSnapshot>? delayed = null;
        bool delayNext = false;
        shop.State.Configure(_ => delayNext ? (delayNext = false, delayed = new(TaskCreationOptions.RunContinuationsAsynchronously)).Item2.Task
            : Task.FromResult(JsonSerializer.Deserialize<ShopSnapshot>(JsonSerializer.Serialize(snapshot))!));
        shop.State.ConfigurePurchases(new((_, _) => throw new NotSupportedException(), (_, _) => throw new NotSupportedException()));
        shop.State.ConfigureConversions(new((_, _) => throw new NotSupportedException()));
        await shop.State.RefreshAsync(); await Pump();
        ItemsControl products = Get<ItemsControl>(shop, "ProductList");
        DependencyObject card = products.ItemContainerGenerator.ContainerFromIndex(0);
        for (int i = 0; i < 3; ++i) { await shop.State.PollPurchaseAsync(); await Pump(); }
        Check(card is not null && ReferenceEquals(card, products.ItemContainerGenerator.ContainerFromIndex(0)),
            "Unchanged polling preserves the actual product containers, avoiding teardown/reload flicker.");
        foreach (var offer in shop.State.Offers.ToArray())
        {
            shop.State.OpenService(offer); await Pump();
            Check(Get<ComboBox>(shop, "CharacterPicker").Visibility == Visibility.Collapsed,
                "All native account services omit the launcher beneficiary picker.");
            if (offer.Offer.Id != "character-rename") Check(!Get<Button>(shop, "PurchaseButton").IsEnabled,
                "Future services cannot be purchased before the realm supports them.");
        }
        shop.State.OpenService(shop.State.Offers[0]); await Pump();
        ListBox prices = Get<ListBox>(shop, "CurrencyChoices");
        DependencyObject priceCard = prices.ItemContainerGenerator.ContainerFromIndex(0);
        delayNext = true; Task poll = shop.State.PollPurchaseAsync(); await Pump();
        Check(prices.IsEnabled && Get<Button>(shop, "PurchaseButton").IsEnabled,
            "The purchase controls remain enabled during a background read.");
        prices.SelectedIndex = 1; await Pump(); delayed!.SetResult(snapshot); await poll; await Pump();
        Check(prices.SelectedIndex == 1 && priceCard is not null && ReferenceEquals(priceCard, prices.ItemContainerGenerator.ContainerFromIndex(0)),
            "A completed background read preserves the changed selection and actual currency containers.");
        shop.State.OpenConversion(); await Pump();
        ShopConversionViewV2 conversion = shop.ConversionPage;
        TextBox amount = Get<TextBox>(conversion, "ConversionAmount");
        amount.Text = "123"; amount.CaretIndex = 2; await Pump();
        delayNext = true; poll = shop.State.PollPurchaseAsync(); await Pump();
        Check(amount.IsEnabled && Get<ComboBox>(conversion, "ConversionCharacterPicker").IsEnabled
            && Get<Button>(conversion, "ConvertButton").IsEnabled, "Conversion inputs remain interactive during a delayed poll.");
        amount.Text = "1234"; amount.CaretIndex = 3;
        delayed!.SetResult(snapshot); await poll; await Pump();
        Check(amount.Text == "1234" && amount.CaretIndex == 3, "Polling preserves the actual textbox draft and caret.");
        foreach (string locale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
        {
            LauncherLocalization.SetLocale(locale);
            foreach ((int width, int height) in new[] { (1586, 992), (1080, 680) })
            {
                shell.Width = width; shell.Height = height; await Pump();
                Grid flow = Get<Grid>(conversion, "ConversionFlow");
                Border source = Get<Border>(conversion, "GoldSourceCard"), result = Get<Border>(conversion, "CreditResultCard");
                Check(flow.Children.OfType<Border>().Count() == 2 && source.ActualWidth > result.ActualWidth
                    && Grid.GetColumn(result) == 2, "The converter has exactly two outer panels, with input inside the gold panel.");
                Check(Descendants(source).OfType<Image>().Any(i => i.Source is BitmapImage bitmap
                    && bitmap.UriSource.ToString().EndsWith("/Gold_coins.png") && bitmap.PixelWidth > 0),
                    "The supplied PNG is decoded and rendered in the available gold panel.");
                Check(Get<Image>(conversion, "RemainingGoldCoin") is { IsVisible: true, Source: BitmapImage },
                    "The remaining gold amount also displays the supplied gold icon.");
                Check(Descendants(conversion).OfType<ScrollViewer>().All(v => v.ScrollableWidth == 0), "No horizontal conversion overflow.");
                Check(result.TranslatePoint(new Point(result.ActualWidth, 0), conversion).X <= conversion.ActualWidth,
                    "Both panels fit the conversion viewport.");
                Capture(shell, Path.Combine(captures, $"shop-local-conversion-{locale}-{width}.png"));
            }
        }
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        shop.State.OpenService(shop.State.Offers.Single(o => o.Offer.Id == "character-faction-change")); await Pump();
        Capture(shell, Path.Combine(captures, "shop-local-account-service-fr-1080.png"));
    }
}
