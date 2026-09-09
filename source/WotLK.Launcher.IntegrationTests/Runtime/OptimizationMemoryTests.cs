using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.Account;
using WotLK.Launcher.UI.V2.Localization;

internal static class OptimizationMemoryTests
{
    internal static async Task<int> RunAsync()
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            string locale = LauncherLocalization.CurrentLocale;
            int result = 0;
            try
            {
                ValidateBitmapBudget();
                ValidateLocalizationLifetime();
            }
            catch (Exception error) { Console.Error.WriteLine(error); result = 1; }
            finally
            {
                LauncherLocalization.SetLocale(locale);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            if (result == 0) Console.WriteLine("Optimization memory PASS: bounded decoded-image LRU, late publication after dispose, collectable detached translated controls and language round-trips; no window shown.");
            completion.TrySetResult(result);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void ValidateBitmapBudget()
    {
        BitmapSource image = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, new byte[64 * 64 * 4], 64 * 4);
        image.Freeze();
        using AvatarBitmapMemoryCache<int> cache = new(2 * 64 * 64 * 4, 3);
        cache.Store(1, image);
        cache.Store(2, image);
        Check(cache.TryGetValue(1, out BitmapSource? held) && ReferenceEquals(held, image), "Memory hits reuse frozen pixels.");
        cache.Store(3, image);
        Check(!cache.TryGetValue(2, out _) && cache.TryGetValue(1, out _) && cache.TryGetValue(3, out _), "Eviction removes the least recently used entry.");
        for (int i = 4; i < 1000; i++) cache.Store(i, image);
        Check(cache.Count == 2 && cache.RetainedBytes == 2 * 64 * 64 * 4, "Retained bytes plateau across a thousand profiles.");
        Check(image.PixelWidth == 64, "Eviction does not invalidate an avatar still held by its view.");
        cache.Dispose();
        cache.Store(1001, image);
        Check(cache.Count == 0 && cache.RetainedBytes == 0, "Late asynchronous publications cannot repopulate a disposed account cache.");
        using AvatarBitmapMemoryCache<int> entryLimit = new(1024 * 1024, 1);
        entryLimit.Store(1, image); entryLimit.Store(2, image);
        Check(entryLimit.Count == 1, "Small thumbnails obey the entry-count limit too.");
    }

    private static void ValidateLocalizationLifetime()
    {
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        Window window = new() { Content = new StackPanel() };
        StackPanel panel = (StackPanel)window.Content;
        using LauncherLocalizationBridge bridge = new(window);
        TextBlock kept = new() { Text = "Paramètres" };
        panel.Children.Add(kept);
        bridge.Refresh();
        LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale);
        Check(kept.Text == "Settings", "The live control is translated.");
        panel.Children.Remove(kept);
        kept.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, kept));
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        panel.Children.Add(kept);
        kept.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, kept));
        Check(kept.Text == "Paramètres", "Recycled controls recover the original French text after a locale change while detached.");

        LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale);
        // A template creates descendants after its parent has already been discovered.
        StackPanel newRow = new();
        TextBlock newText = new() { Text = "Paramètres" };
        newRow.Children.Add(newText);
        panel.Children.Add(newRow);
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, panel));
        DispatcherFrame frame = new();
        _ = panel.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        Check(newText.Text == "Settings", "New descendants are translated when their existing page loads, without a manual refresh.");
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        Check(newText.Text == "Paramètres", "Deferred descendants retain the French source for later locale changes.");

        List<WeakReference> removed = ReplaceRows(panel, bridge);
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(removed.All(reference => !reference.IsAlive), "Removed translated rows are collectable while the shell and bridge remain alive.");
        GC.KeepAlive(bridge);
        GC.KeepAlive(window);
        window.Close();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> ReplaceRows(StackPanel panel, LauncherLocalizationBridge bridge)
    {
        List<WeakReference> removed = [];
        for (int batch = 0; batch < 10; batch++)
        {
            for (int i = 0; i < 30; i++)
            {
                TextBlock row = new() { Text = "Paramètres" };
                panel.Children.Add(row);
                removed.Add(new WeakReference(row));
            }
            bridge.Refresh();
            panel.Children.Clear();
            bridge.Refresh();
        }
        return removed;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
