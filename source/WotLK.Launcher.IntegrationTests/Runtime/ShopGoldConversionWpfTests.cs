using System.IO;
using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ShopWpfTests
{
    private static async Task VerifyGoldConversionAsync(LauncherShellV2 shell, ShopViewV2 shop, string directory)
    {
        foreach (string locale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
        {
            LauncherLocalization.SetLocale(locale);
            ShopSnapshot current = ShopRuntimeTests.Snapshot with { CreditBalanceEuroCents = 0, EuroBalanceCents = 1234, Conversions = new(true, []) };
            TaskCompletionSource<ShopGoldConversion> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ShopCreateGoldConversion? request = null;
            shop.State.Configure(_ => Task.FromResult(current));
            shop.State.ConfigureConversions(new((input, _) => { request = input; return response.Task; }));
            await shop.State.RefreshAsync(); shop.State.OpenConversion(); await Pump();
            ShopConversionViewV2 page = shop.ConversionPage;
            TextBox amount = Get<TextBox>(page, "ConversionAmount");
            Button convert = Get<Button>(page, "ConvertButton");
            ComboBox character = Get<ComboBox>(page, "ConversionCharacterPicker");
            amount.Text = "10"; await Pump();
            Check(convert.IsEnabled && character.IsEnabled && amount.IsEnabled && !shop.State.IsConversionPreview,
                "Real conversion bindings enable eligible gold and do not depend on preview mode.");
            convert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            Check(request is { OfferedCopper: 100000, ExpectedCreditCents: 10 } && shop.State.IsConverting
                && !convert.IsEnabled && !character.IsEnabled && !amount.IsEnabled && !shop.State.HasConversionReceipt,
                "Clicking the real action freezes every draft input until the response, without fabricating a receipt.");
            Capture(shell, Path.Combine(directory, "gold-pending-" + locale + ".png"));
            Get<Button>(page, "CloseConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
            Check(!shop.State.IsConversionOpen && shop.State.IsConverting, "Closing the converter does not cancel a potentially committed server operation.");
            ShopGoldConversion pending = new(Guid.NewGuid().ToString("N"), request!.IdempotencyKey, 101, "Asteria", 100000, 10,
                "pending", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            ShopGoldConversion completed = pending with { Status = "completed", GoldBeforeCopper = 4235067, GoldAfterCopper = 4135067, CreditBeforeCents = 0, CreditAfterCents = 10 };
            current = current with { CreditBalanceEuroCents = 10, Characters = [current.Characters[0] with { GoldCopper = 4135067 }], Conversions = new(true, [completed]) };
            response.SetResult(pending);
            for (int i = 0; i < 100 && shop.State.IsConverting; i++) { await Task.Delay(20); await Pump(); }
            shop.State.OpenConversion(); await Pump();
            Check(!shop.State.IsConverting && shop.State.HasConversionReceipt && amount.Text == "" && character.IsEnabled && amount.IsEnabled
                && !convert.IsEnabled && shop.State.AvailableCopper == 4135067 && shop.State.CreditBalance == ShopUiState.FormatEuros(10),
                "The authoritative completion restores input bindings and shows its exact balances after navigation.");
            await Task.Delay(1000); await Pump(); // Capture the completed header animation.
            Capture(shell, Path.Combine(directory, "gold-completed-" + locale + ".png"));
            amount.Text = "5"; await Pump();
            Check(convert.IsEnabled && !shop.State.HasConversionReceipt, "A new amount re-enables conversion after a terminal receipt.");
            shop.State.ResetSession(); await Pump();
            Check(!shop.State.HasPendingConversion && !shop.State.IsConversionOpen, "Signing out clears the real conversion view.");
        }
    }
}
