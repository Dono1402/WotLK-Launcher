using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ArmoryViewV2
{
    private void DetachBrowserSession()
    {
        Action? detach = _detachBrowserEvents;
        _detachBrowserEvents = null;
        try { detach?.Invoke(); }
        catch (InvalidOperationException) { } // WPF may already have disposed the controller on window close.
    }

    private void BindBrowserSession(WebView2CompositionControl browser, LauncherArmoryLocalHost host,
        CoreWebView2Environment environment, CancellationToken token)
    {
        CoreWebView2 core = browser.CoreWebView2;
        DetachBrowserSession();
        ulong navigationId = 0;
        core.WebResourceRequested += ResourceRequested;
        core.NavigationStarting += NavigationStarting;
        core.FrameNavigationStarting += FrameStarting;
        core.NewWindowRequested += NewWindow;
        core.PermissionRequested += Permission;
        core.DownloadStarting += Download;
        core.WebMessageReceived += MessageReceived;
        core.NavigationCompleted += NavigationCompleted;
        core.ProcessFailed += ProcessFailed;
        _detachBrowserEvents = () =>
        {
            core.WebResourceRequested -= ResourceRequested;
            core.NavigationStarting -= NavigationStarting;
            core.FrameNavigationStarting -= FrameStarting;
            core.NewWindowRequested -= NewWindow;
            core.PermissionRequested -= Permission;
            core.DownloadStarting -= Download;
            core.WebMessageReceived -= MessageReceived;
            core.NavigationCompleted -= NavigationCompleted;
            core.ProcessFailed -= ProcessFailed;
        };

        void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            if (!token.IsCancellationRequested && host.Owns(args.Request.Uri)) args.Request.Headers.SetHeader("X-Atlas-Armory-Key", host.Key);
            else args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        }
        void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            args.Cancel = token.IsCancellationRequested || !host.Owns(args.Uri);
            if (!args.Cancel) navigationId = args.NavigationId;
        }
        void FrameStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) =>
            args.Cancel = token.IsCancellationRequested || args.Uri != "about:blank" && !host.Owns(args.Uri);
        void NewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
        void Permission(object? sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;
        void Download(object? sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;
        void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args) =>
            HandleProfileMessage(args.Source, args.WebMessageAsJson, host, token);
        void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (token.IsCancellationRequested || args.NavigationId != navigationId || navigationId == 0) return;
            ProfileLoadingIndicator.Visibility = Visibility.Collapsed;
            bool ready = args.IsSuccess && host.Owns(core.Source);
            StatusPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            if (ready) { browser.Visibility = Visibility.Visible; PublishProfile(); }
            else ShowFailure();
        }
        void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
        {
            if (token.IsCancellationRequested || !ReferenceEquals(browser, _browser)) return;
            // WebView forbids disposing its controller inside a browser callback.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (token.IsCancellationRequested || !ReferenceEquals(browser, _browser)) return;
                ResetSession(preserveSharedCharacter: true, preserveDraft: true);
                ShowFailure();
            }));
        }
    }
}
