using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.Dashboard;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class GameGatewayMetricsWpfTests
{
    internal static async Task RunAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => Render(completion)) { IsBackground = true, Name = "AtlasMetricsMemoryRender" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void Render(TaskCompletionSource completion)
    {
        const double launcherWidth = 1597.6, launcherHeight = 996.8, topBarHeight = 124;
        Application? application = null;
        Window? logicalHost = null;
        string priorLocale = LauncherLocalization.CurrentLocale;
        try
        {
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string path in new[]
            {
                "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
                "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
                "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
            }) application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(path, UriKind.Relative) });

            LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
            DashboardUiState dashboard = new();
            dashboard.ApplyView(DashboardStateAdapter.Project(DashboardSnapshot.Initial with
            {
                RealmState = DashboardRealmState.Online, OnlinePlayers = 23,
                OnlinePlayerCountKind = "excluding-random-bots", GatewayLatencyMilliseconds = 47
            }));
            GameViewV2 view = new()
            {
                State = LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready), DashboardState = dashboard,
                Width = launcherWidth, Height = launcherHeight - topBarHeight
            };
            // Logical ownership is sufficient for the real localization bridge.
            // No Show(), EnsureHandle(), HwndSource, tray, game or native window is used.
            logicalHost = new Window { Width = launcherWidth, Height = launcherHeight, Content = view };
            using LauncherLocalizationBridge bridge = new(logicalHost);
            string directory = Path.GetFullPath("artifacts/atlas-social-corrections/metrics-captures");
            Directory.CreateDirectory(directory);
            foreach ((string locale, string suffix, string playersLabel, string latencyLabel) in new[]
            {
                (LauncherLocalization.FrenchLocale, "fr", "joueurs en ligne", "latence"),
                (LauncherLocalization.EnglishLocale, "en", "players online", "latency")
            })
            {
                LauncherLocalization.SetLocale(locale);
                Layout();
                bridge.Refresh();
                Layout();
                Grid facts = (Grid)view.FindName("RealmFacts");
                StackPanel players = (StackPanel)facts.Children[0];
                Border latencyBorder = (Border)facts.Children[1];
                StackPanel latency = (StackPanel)latencyBorder.Child;
                Require(((TextBlock)players.Children[0]).Text == "23" && ((TextBlock)latency.Children[0]).Text == "47 ms",
                    "The real view must display the measured player count and TCP latency.");
                Require(((TextBlock)players.Children[1]).Text == playersLabel && ((TextBlock)latency.Children[1]).Text == latencyLabel,
                    "The real localization bridge must translate both metrics captions.");
                Require(((StackPanel)view.FindName("HeroCopyContent")).Children.Count == 3,
                    "The hero must contain the realm, title and subtitle only; the three chips must be absent.");
                Require(new WindowInteropHelper(logicalHost).Handle == IntPtr.Zero && PresentationSource.FromVisual(view) is null,
                    "In-memory rendering must not create an HWND or connect to a desktop presentation source.");
                Require(Math.Abs(view.ActualWidth - launcherWidth) < 1 && Math.Abs(view.ActualHeight - (launcherHeight - topBarHeight)) < 1,
                    "Only the Game region beneath the fixed 124-DIP top bar may be rendered.");
                string file = Path.Combine(directory, $"game-metrics-{suffix}.png");
                RenderTargetBitmap bitmap = new((int)Math.Ceiling(launcherWidth), (int)Math.Ceiling(launcherHeight - topBarHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                PngBitmapEncoder encoder = new();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using FileStream output = File.Create(file);
                encoder.Save(output);
                Console.WriteLine($"Game metrics {suffix} memory render: {file}; launcher 1597.6x996.8, game area 1597.6x872.8, HWND=0.");
            }
            completion.TrySetResult();

            void Layout()
            {
                Size size = new(launcherWidth, launcherHeight - topBarHeight);
                view.Measure(size);
                view.Arrange(new Rect(size));
                view.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally
        {
            LauncherLocalization.SetLocale(priorLocale);
            logicalHost?.Close();
            application?.Shutdown();
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
