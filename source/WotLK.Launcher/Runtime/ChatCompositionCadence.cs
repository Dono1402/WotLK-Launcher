using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace WotLK.Launcher.Runtime;

/// <summary>
/// Removes the pinned composition control's 16 ms capture interval. Chromium
/// still produces frames at the display cadence; this does not generate frames.
/// </summary>
internal sealed class ChatCompositionCadence : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Version SupportedSdk = new(1, 0, 3856, 49);
    private readonly WebView2CompositionControl _browser;
    private readonly FieldInfo _imageField;
    private readonly FieldInfo _sessionField;
    private readonly DispatcherTimer _timer;
    private object? _lastSession;
    private object? _adjustedSession;
    private long _originalTicks;
    private bool _disposed;

    internal static ChatCompositionCadence? Attach(WebView2CompositionControl browser)
    {
        browser.Dispatcher.VerifyAccess();
        // Private field access is confined to the exact package audited here.
        // A package upgrade or older Windows keeps Microsoft's default behavior.
        if (typeof(WebView2CompositionControl).Assembly.GetName().Version != SupportedSdk
            || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) return null;
        FieldInfo? imageField = typeof(WebView2CompositionControl).GetField("_d3dImage", PrivateInstance);
        FieldInfo? sessionField = imageField?.FieldType.GetField("_session", PrivateInstance);
        return imageField is null || sessionField is null ? null : new(browser, imageField, sessionField);
    }

    private ChatCompositionCadence(WebView2CompositionControl browser, FieldInfo imageField, FieldInfo sessionField)
    {
        _browser = browser;
        _imageField = imageField;
        _sessionField = sessionField;
        // This checks for recreated capture sessions, never requests rendering.
        _timer = new DispatcherTimer(DispatcherPriority.Background, browser.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += TimerTick;
        browser.Loaded += Loaded;
        browser.Unloaded += Unloaded;
        browser.SizeChanged += SizeChanged;
        browser.IsVisibleChanged += VisibilityChanged;
        Resume();
    }

    private void Loaded(object sender, RoutedEventArgs args) => Resume();
    private void Unloaded(object sender, RoutedEventArgs args) { _timer.Stop(); Restore(); _lastSession = null; }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Resume();
    private void TimerTick(object? sender, EventArgs args) => Apply();
    private void SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // The control recreates its capture session during its layout update.
        _ = _browser.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Apply));
    }

    private void Resume()
    {
        if (_disposed) return;
        if (_browser.IsLoaded && _browser.IsVisible) { Apply(); _timer.Start(); }
        else _timer.Stop();
    }

    private void Apply()
    {
        if (_disposed || !_browser.IsLoaded || !_browser.IsVisible) return;
        try
        {
            object? image = _imageField.GetValue(_browser);
            object? session = image is null ? null : _sessionField.GetValue(image);
            if (session is null || ReferenceEquals(session, _lastSession)) return;
            Restore();
            _lastSession = session;
            if (TrySetInterval(session, 0, out long previous)) { _adjustedSession = session; _originalTicks = previous; }
        }
        catch (Exception) { /* Compatibility must never prevent chat startup. */ }
    }

    private void Restore()
    {
        object? session = _adjustedSession;
        _adjustedSession = null;
        if (session is not null) TrySetInterval(session, _originalTicks, out _);
    }

    private static bool TrySetInterval(object session, long ticks, out long previous)
    {
        previous = 0;
        IntPtr native = IntPtr.Zero;
        try
        {
            if (session is not WinRT.IWinRTObject winrt) return false;
            // Official Windows ABI: IInspectable slots 0..5, followed by
            // get/put_MinUpdateInterval. TimeSpan is an Int64 of 100 ns ticks.
            // https://github.com/microsoft/windows-rs/blob/master/crates/libs/windows/src/Windows/Graphics/Capture/mod.rs
            Guid interfaceId = new("67c0ea62-1f85-5061-925a-239be0ac09cb");
            if (Marshal.QueryInterface(winrt.NativeObject.ThisPtr, ref interfaceId, out native) < 0) return false;
            IntPtr table = Marshal.ReadIntPtr(native);
            GetInterval get = Marshal.GetDelegateForFunctionPointer<GetInterval>(Marshal.ReadIntPtr(table, 6 * IntPtr.Size));
            SetInterval set = Marshal.GetDelegateForFunctionPointer<SetInterval>(Marshal.ReadIntPtr(table, 7 * IntPtr.Size));
            return get(native, out previous) >= 0 && set(native, ticks) >= 0;
        }
        catch (Exception) { return false; }
        finally { if (native != IntPtr.Zero) Marshal.Release(native); }
    }

    public void Dispose()
    {
        _browser.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= TimerTick;
        _browser.Loaded -= Loaded;
        _browser.Unloaded -= Unloaded;
        _browser.SizeChanged -= SizeChanged;
        _browser.IsVisibleChanged -= VisibilityChanged;
        Restore();
        _lastSession = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterval(IntPtr instance, out long value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetInterval(IntPtr instance, long value);
}
