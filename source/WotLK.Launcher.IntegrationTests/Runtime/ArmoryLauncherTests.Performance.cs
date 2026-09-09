using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WotLK.Launcher.Account;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ArmoryLauncherTests
{
    // Each invocation is one fresh process. Only this explicitly supplied fixture root is reused.
    internal static async Task<int> RunPerformanceAsync(string directory, string cacheMode)
    {
        string root = Path.GetFullPath(directory);
        string marker = Path.Combine(root, "atlas-performance-fixture.txt");
        if (cacheMode == "cold")
        {
            if (Directory.Exists(root)) throw new IOException("Cold benchmark requires a new fixture directory.");
            Directory.CreateDirectory(root);
            File.WriteAllText(marker, "Synthetic launcher performance fixture; no user session.");
        }
        else if (cacheMode != "warm" || !File.Exists(marker) || !Directory.Exists(Path.Combine(root, "webview-chat")))
            throw new IOException("Warm benchmark requires an existing marked benchmark fixture.");

        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            Dispatcher.Run();
            async Task ExecuteAsync()
            {
                Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                try
                {
                    object report = await MeasurePerformanceAsync(application, root, cacheMode);
                    await File.WriteAllTextAsync(Path.Combine(root, cacheMode + ".json"),
                        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                    completion.TrySetResult(0);
                }
                catch (Exception error) { Console.Error.WriteLine(error); completion.TrySetResult(1); }
                finally { application.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
        }) { IsBackground = true, Name = "AtlasPerformanceOffscreenFixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromMinutes(5));
    }

    private static async Task<object> MeasurePerformanceAsync(Application application, string root, string cacheMode)
    {
        Stopwatch startup = Stopwatch.StartNew();
        using ArmoryFixture fixture = new();
        True(LauncherWebViewRuntime.IsSupported(LauncherWebViewRuntime.InstalledVersion()),
            "Benchmark requires the installed WebView runtime and must never install it.");
        foreach (string resource in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WotLK.Launcher;component/" + resource, UriKind.Relative) });
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        double fixtureSetupMs = startup.Elapsed.TotalMilliseconds;
        AccountUiState state = ConnectedAccount("Aster");
        LauncherShellV2 shell = CreateShell(state);
        ArmoryViewV2 armory = Required<ArmoryViewV2>(shell, "ArmoryView");
        ChatViewV2 chat = shell.ChatPage;
        byte[] image = await File.ReadAllBytesAsync(fixture.AvatarFixturePath);
        chat.RichUserDataFolder = Path.Combine(root, "webview-chat");
        chat.MediaResolver = (key, _, _) => Task.FromResult<ChatMediaStream?>(
            key is "attachments/fixture-image" or "avatars/42/1" or "avatars/91/1"
                ? new(new MemoryStream(image, writable: false), "image/png", image.Length) : null);
        chat.ApplyRichSnapshot(ChatFullShellWpfTests.Snapshot("fr", image.Length));
        chat.SetRichMode(true);
        armory.Configure(_ => Task.FromResult<uint?>(42), state, () => fixture.Configuration,
            Path.Combine(root, "webview-armory"), new ArmoryBannerStore(Path.Combine(root, "banners")));
        HashSet<int> ownedPids = [Environment.ProcessId];
        List<object> phases = [];
        Dictionary<string, double> timings = [];
        List<double> navigationMs = [], friendProfileMs = [], ownProfileMs = [];
        List<object> navigationDetails = [];
        List<WeakReference> retiredBrowsers = [];
        bool diagnostics = Environment.GetEnvironmentVariable("ATLAS_LAUNCHER_PERF_DIAGNOSTICS") == "1";
        const string chatReady = "document.querySelector('#thread-title')?.textContent==='Lyra'&&document.fonts.status==='loaded'&&[...document.images].filter(i=>i.hasAttribute('src')).every(i=>i.complete&&i.naturalWidth>0)";
        string modelReady = "document.getElementById('character-view')?.contentWindow.armory?.ready===true";
        if (fixture.HasModel) modelReady += "&&document.getElementById('character-view').contentWindow.armory.root.children.length>0&&document.getElementById('character-view').contentWindow.armory.frames>2";
        try
        {
            ShowOffscreen(shell);
            await PumpAsync();
            timings["shellConstructConfigureLayoutMs"] = startup.Elapsed.TotalMilliseconds - fixtureSetupMs;
            using (Process process = Process.GetCurrentProcess())
                timings["processToShellLayoutMs"] = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
            if (diagnostics && Environment.GetEnvironmentVariable("ATLAS_LAUNCHER_PERF_NATIVE_ONLY") == "1")
            {
                for (int cycle = 0; cycle < 3; cycle++)
                    foreach (string button in new[] { "AddonsNavigationButton", "PatchNotesNavigationButton", "SettingsButton", "GameNavigationButton" })
                    {
                        long allocations = GC.GetAllocatedBytesForCurrentThread();
                        Stopwatch nativeAction = Stopwatch.StartNew();
                        Navigate(button);
                        double clickMs = nativeAction.Elapsed.TotalMilliseconds;
                        await PumpAsync();
                        navigationDetails.Add(new { cycle, button, clickMs, totalMs = nativeAction.Elapsed.TotalMilliseconds,
                            uiAllocatedMiB = (GC.GetAllocatedBytesForCurrentThread() - allocations) / 1048576d });
                    }
                return new { diagnostics, nativeOnly = true, cacheMode, timings, navigationDetails };
            }
            phases.Add(await IdleAsync("shell"));
            Stopwatch action = Stopwatch.StartNew();
            Navigate("MessagesNavigationButton");
            await ReadyAsync(() => chat.RichBrowser?.CoreWebView2, chatReady);
            timings["firstMessagesMs"] = action.Elapsed.TotalMilliseconds;
            phases.Add(await IdleAsync("messages"));
            action.Restart();
            await OpenProfileAsync(shell);
            await ReadyAsync(() => armory.Browser?.CoreWebView2, modelReady);
            timings["firstOwnProfileMs"] = action.Elapsed.TotalMilliseconds;
            phases.Add(await IdleAsync("own-profile"));

            for (int cycle = 0; cycle < 20; cycle++)
            {
                foreach (string button in new[] { "GameNavigationButton", "AddonsNavigationButton", "PatchNotesNavigationButton", "SettingsButton", "MessagesNavigationButton" })
                {
                    long allocations = GC.GetAllocatedBytesForCurrentThread();
                    action.Restart();
                    Navigate(button);
                    double clickMs = action.Elapsed.TotalMilliseconds;
                    await PumpAsync();
                    if (button == "MessagesNavigationButton") await ReadyAsync(() => chat.RichBrowser?.CoreWebView2, chatReady);
                    navigationMs.Add(action.Elapsed.TotalMilliseconds);
                    navigationDetails.Add(new { cycle, button, clickMs, totalMs = action.Elapsed.TotalMilliseconds,
                        uiAllocatedMiB = (GC.GetAllocatedBytesForCurrentThread() - allocations) / 1048576d });
                }
                action.Restart();
                await OpenProfileAsync(shell);
                await ReadyAsync(() => armory.Browser?.CoreWebView2, modelReady);
                ownProfileMs.Add(action.Elapsed.TotalMilliseconds);
            }
            phases.Add(await IdleAsync("own-profile-after-20-navigation-cycles"));

            // A separate authenticated RPC fixture exercises actual friend helper/cache replacement.
            Navigate("GameNavigationButton");
            armory.Configure(_ => Task.FromResult<uint?>(42), state,
                () => fixture.AuthenticatedConfiguration with { DataRoot = Path.Combine(root, "friend-data") },
                Path.Combine(root, "webview-armory"), new ArmoryBannerStore(Path.Combine(root, "banners")),
                readData: (owner, request, token) => ReadFriend(owner, owner, request, token), readFriendData: ReadFriend);
            FriendRuntimeItem first = new(91, "Friend91", null, null, FriendRelationship.Accepted, false,
                null, null, null, null, null, "Fixture", "Fixture", IsLauncherOnline: true);
            shell.FriendsState.ApplyRuntimeView(FriendsStateAdapter.Project(FriendsRuntimeSnapshot.SignedOut with
            {
                CurrentUserId = 42, IsAuthenticated = true, LoadState = FriendsLoadState.Loaded,
                Friends = [first, first with { AccountId = 92, Username = "Friend92" }]
            }));
            for (int cycle = 0; cycle <= 20; cycle++)
            {
                uint target = cycle % 2 == 0 ? 91u : 92u;
                WeakReference? previousBrowser = armory.Browser is { } currentBrowser ? new WeakReference(currentBrowser) : null;
                action.Restart();
                typeof(LauncherShellV2).GetMethod("FriendsDrawer_PublicProfileRequested", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(shell, [shell, new FriendPublicProfileRequestedEventArgs(target, "Friend" + target)]);
                await ReadyAsync(() => armory.Browser?.CoreWebView2,
                    $"document.getElementById('profile-name')?.textContent==='Friend{target}'&&document.querySelector('.character strong')?.textContent==='Mage{target}'");
                if (cycle == 0) timings["firstFriendRosterMs"] = action.Elapsed.TotalMilliseconds;
                else friendProfileMs.Add(action.Elapsed.TotalMilliseconds);
                if (previousBrowser is not null && !ReferenceEquals(previousBrowser.Target, armory.Browser)) retiredBrowsers.Add(previousBrowser);
                RememberProcesses();
                if (cycle is 0 or 10 or 20) phases.Add(await IdleAsync("friend-profile-after-" + cycle + "-switches"));
                AssertOffscreen(shell);
            }
            int retiredBeforeGc = retiredBrowsers.Count(reference => reference.IsAlive);
            if (diagnostics)
            {
                // Diagnostic only: separate retained controls from uncollected temporary allocations.
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await PumpAsync();
                phases.Add(await IdleAsync("diagnostic-after-forced-gc"));
            }
            return new
            {
                cacheMode, utc = DateTime.UtcNow, framework = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
                logicalProcessors = Environment.ProcessorCount, webViewVersion = LauncherWebViewRuntime.InstalledVersion(),
                method = "Release integration process; WPF offscreen inactive WS_EX_NOACTIVATE; local synthetic state/media/RPC; private committed bytes summed across owned processes; no authentication, network service, disk-cache flush or live desktop input.",
                fixtureSetupMs, hasRendered3DModel = fixture.HasModel, friendFixtureHas3DModel = false,
                timings, navigationMs, navigationDetails, ownProfileMs, friendProfileMs, phases,
                diagnostics, retiredBeforeGc, retiredAfterGc = diagnostics ? retiredBrowsers.Count(reference => reference.IsAlive) : (int?)null
            };
        }
        finally
        {
            RememberProcesses();
            shell.Close();
            await armory.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(20));
            await PumpAsync();
            // Only PIDs obtained from our WebView environments and our own Node host are inspected.
            Stopwatch cleanup = Stopwatch.StartNew();
            while (ownedPids.Any(pid => pid != Environment.ProcessId && IsRunning(pid)) && cleanup.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(100);
            True(!ownedPids.Any(pid => pid != Environment.ProcessId && IsRunning(pid)), "All benchmark child processes must stop after shell disposal.");
            Console.WriteLine($"Performance {cacheMode}: child process cleanup verified.");
        }

        void Navigate(string name) => Required<Button>(shell, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void RememberProcesses()
        {
            foreach (CoreWebView2? core in new[] { chat.RichBrowser?.CoreWebView2, armory.Browser?.CoreWebView2 })
                if (core is not null) foreach (CoreWebView2ProcessInfo info in core.Environment.GetProcessInfos()) ownedPids.Add(info.ProcessId);
            object? host = typeof(ArmoryViewV2).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(armory);
            if (host is not null && typeof(LauncherArmoryLocalHost).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host) is Process node)
                ownedPids.Add(node.Id);
        }
        Dictionary<int, (long Private, long Working, double CpuMs)> Sample()
        {
            RememberProcesses();
            Dictionary<int, (long, long, double)> values = [];
            foreach (int pid in ownedPids)
                try
                {
                    using Process p = Process.GetProcessById(pid);
                    if (!p.HasExited) values[pid] = (p.PrivateMemorySize64, p.WorkingSet64, p.TotalProcessorTime.TotalMilliseconds);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            return values;
        }
        async Task<object> IdleAsync(string name)
        {
            // Allow asynchronous layout/render work to settle, without forcing GC or trimming memory.
            await Task.Delay(1500);
            Dictionary<int, (long Private, long Working, double CpuMs)> before = Sample();
            Stopwatch elapsed = Stopwatch.StartNew();
            List<double> privateMiB = [], workingMiB = [];
            double shellPrivateMiB = 0;
            for (int sample = 0; sample < (diagnostics ? 2 : 16); sample++)
            {
                await Task.Delay(500);
                var values = Sample();
                privateMiB.Add(values.Values.Sum(v => v.Private) / 1048576d);
                workingMiB.Add(values.Values.Sum(v => v.Working) / 1048576d);
                shellPrivateMiB = values[Environment.ProcessId].Private / 1048576d;
            }
            var after = Sample();
            double cpuMs = after.Sum(pair => before.TryGetValue(pair.Key, out var prior) ? Math.Max(0, pair.Value.CpuMs - prior.CpuMs) : 0);
            double oneCorePercent = cpuMs / elapsed.Elapsed.TotalMilliseconds * 100;
            Console.WriteLine($"Performance {cacheMode} {name}: private {privateMiB.Average():F1} MiB; CPU {oneCorePercent / Environment.ProcessorCount:F3}% machine; {after.Count} processes.");
            return new { name, durationMs = elapsed.Elapsed.TotalMilliseconds, samples = privateMiB.Count,
                privateMiBMean = privateMiB.Average(), privateMiBMax = privateMiB.Max(), shellPrivateMiB,
                workingSetSumMiBMean = workingMiB.Average(), cpuOneCorePercent = oneCorePercent,
                cpuMachinePercent = oneCorePercent / Environment.ProcessorCount, processCount = after.Count,
                cpuProcessSetStable = before.Keys.ToHashSet().SetEquals(after.Keys), privateMiBSamples = privateMiB };
        }
    }

    private static async Task ReadyAsync(Func<CoreWebView2?> getCore, string expression)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(40))
        {
            CoreWebView2? core = getCore();
            if (core is not null && await core.ExecuteScriptAsync("(()=>{try{return Boolean(" + expression + ")}catch{return false}})()") == "true") return;
            await Task.Delay(15);
        }
        throw new TimeoutException("Performance fixture did not become ready: " + expression);
    }

    private static Task<JsonElement> ReadFriend(uint owner, uint target, LauncherArmoryDataRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(request.Operation == "catalog"
            ? JsonSerializer.SerializeToElement(new { capturedAtUtc = "2026-09-09 00:00:00", items = Array.Empty<object>() })
            : JsonSerializer.SerializeToElement(new { observedAtUtc = "2026-09-09 00:00:00", characters = new[]
            {
                new { character = new { guid = target * 10, name = "Mage" + target, race = 1, classId = 8, gender = 0,
                    level = 80, skin = 0, face = 0, hairStyle = 0, hairColor = 0, facialStyle = 0,
                    online = 0, zoneId = 0, lastLogout = 0 }, equipment = Array.Empty<object>(), snapshot = (object?)null, values = (object?)null }
            } }));
    }

    private static bool IsRunning(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
