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

internal static class ShopWpfTests
{
    internal static async Task<int> RunAsync(string captureDirectory)
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
                    shell.PrepareShopPreview();
                    shop.State.Configure(_ => Task.FromResult(ShopRuntimeTests.Snapshot with { CreditBalanceEuroCents = 265 }));
                    shell.Show(); await Pump();
                    Check(shell.CurrentPage == LauncherShellPage.Shop, "Explicit shop preview starts without a runtime or a real account.");
                    Get<Button>(shell, "ShopNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Pump();
                    Check(shell.CurrentPage == LauncherShellPage.Shop && shop.IsVisible, "Shop opens inside the existing shell.");
                    Check(Get<ListBox>(shop, "ProductList").SelectedItem is not null, "First offer is selected.");
                    ComboBox character = Get<ComboBox>(shop, "CharacterPicker");
                    ComboBox currency = Get<ComboBox>(shop, "CurrencyPicker");
                    Check(character.SelectedIndex == -1, "No beneficiary is chosen implicitly.");
                    character.SelectedIndex = 0; currency.SelectedIndex = 1; await Pump();
                    Check(shop.State.SelectedCharacter?.Character.Guid == 101 && shop.State.SelectedPrice?.Price.Currency == "gold", "WPF pickers update the character and currency.");
                    Check(Get<TextBlock>(shop, "SummaryText").Text.Contains("Asteria"), "Summary reflects the selected beneficiary.");
                    Get<Button>(shop, "RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
                    Check(character.SelectedIndex == 0 && currency.SelectedIndex == 1, "Bindings preserve selection after refresh.");
                    LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
                    shell.Width = 1586; shell.Height = 992; currency.SelectedIndex = 0; await Pump();
                    Get<ScrollViewer>(shop, "PageScroll").ScrollToTop(); await Pump();
                    Capture(shell, Path.Combine(captureDirectory, "shop-reference-1586.png"));
                    Check(Get<Border>(shop, "ConversionBanner").TranslatePoint(new Point(0, Get<Border>(shop, "ConversionBanner").ActualHeight), shell).Y <= shell.ActualHeight, "Reference size shows the whole conversion banner.");
                    Check(Get<ScrollViewer>(shop, "PageScroll").ScrollableHeight == 0, "Reference layout fits without a vertical scrollbar.");
                    currency.SelectedIndex = 1; await Pump();
                    foreach (string selectedLocale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
                    {
                        LauncherLocalization.SetLocale(selectedLocale); await Pump();
                        Check(character.SelectedIndex == 0 && currency.SelectedIndex == 1, "Language switch preserves WPF selections.");
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
                            Check(!Get<Button>(shop, "PurchaseButton").IsEnabled, "Purchases remain closed.");
                            Check(Get<ScrollViewer>(shop, "PageScroll").ScrollableWidth == 0, "No horizontal overflow.");
                            Capture(shell, Path.Combine(captureDirectory, $"shop-{selectedLocale}-{width}.png"));
                        }
                    }
                    currency.SelectedIndex = 0; await Pump();
                    Check(shop.State.PaymentHint.Contains("Bancontact") && shop.State.PriceLabel.Contains("5.00"), "Direct EUR payment lists configured methods without converting into credits.");
                    Capture(shell, Path.Combine(captureDirectory, "shop-en-direct-payment-1080.png"));
                    Get<ScrollViewer>(shop, "PageScroll").ScrollToBottom(); await Pump();
                    Capture(shell, Path.Combine(captureDirectory, "shop-en-bottom-1080.png"));
                    Get<Button>(shop, "OpenConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Get<TextBox>(shop, "ConversionAmount").Text = "212"; await Pump();
                    Check(Get<TextBlock>(shop, "ConversionCreditText").Text == "2.65 €", "212 gold renders EUR 2.65 in the live conversion panel.");
                    Check(!Get<Button>(shop, "ConvertButton").IsEnabled, "Conversion cannot debit gold before server fulfillment exists.");
                    Get<ScrollViewer>(shop, "PageScroll").ScrollToBottom(); await Pump();
                    Capture(shell, Path.Combine(captureDirectory, "shop-en-conversion-1080.png"));
                    LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale); await Pump();
                    Check(Get<TextBlock>(shop, "ConversionCreditText").Text == "2,65 €", "Conversion amount survives language change and uses French formatting.");
                    Capture(shell, Path.Combine(captureDirectory, "shop-fr-conversion-1080.png"));
                    shop.State.Configure(_ => Task.FromResult(ShopRuntimeTests.Snapshot with { Characters = [] }));
                    await shop.State.RefreshAsync(); await Pump();
                    Check(!character.IsEnabled && character.SelectedItem is null, "No-character state disables beneficiary picker and clears prior selection.");
                    shop.State.Configure(_ => Task.FromResult(ShopRuntimeTests.Snapshot with { Offers = [] }));
                    await shop.State.RefreshAsync(); await Pump();
                    Check(Get<TextBlock>(shop, "StatusText").IsVisible && !Get<Button>(shop, "PurchaseButton").IsVisible, "Empty catalog shows a status instead of stale checkout.");
                    Capture(shell, Path.Combine(captureDirectory, "shop-en-empty-1080.png"));
                    Check(errors.Messages.Count == 0, "No WPF binding errors: " + string.Join("\n", errors.Messages));
                    Console.WriteLine("Shop WPF PASS: 1586x992 reference and four adaptive widths, FR/EN, navigation bounds, beneficiary/currency selection and refresh, conversion, empty state, purchase gate; offscreen inactive fixtures and PNG captures only.");
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
        return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

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
