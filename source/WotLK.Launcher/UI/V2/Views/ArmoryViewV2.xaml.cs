using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public sealed class ArmoryProfileSaveRequestedEventArgs(string statusMessage, string bio) : EventArgs
{
    public string StatusMessage { get; } = statusMessage;
    public string Bio { get; } = bio;
    public bool Accepted { get; set; }
}

public sealed class ArmoryAvatarRequestedEventArgs(CancellationToken sessionToken) : EventArgs
{
    public CancellationToken SessionToken { get; } = sessionToken;
    public bool Accepted { get; set; }
}

public sealed class ArmoryHeaderHoverEventArgs(bool hovered) : EventArgs
{
    public bool Hovered { get; } = hovered;
}

public partial class ArmoryViewV2 : UserControl, IDisposable
{
    private Func<CancellationToken, Task<uint?>>? _getAccount;
    private AccountUiState? _state;
    private LauncherArmoryLocalHost? _host;
    private WebView2CompositionControl? _browser;
    private CancellationTokenSource? _lifetime;
    private Func<LauncherArmoryLocalConfiguration> _loadConfiguration = LauncherArmoryLocalHost.LoadConfiguration;
    private Func<uint, LauncherArmoryDataRequest, CancellationToken, Task<JsonElement>>? _readData;
    private string? _userDataFolder;
    private string? _sessionUsername;
    private BitmapSource? _avatarSource;
    private string? _avatarData;
    private string? _profileBridgeError;
    private string? _avatarBridgeError;
    private bool _avatarSelectionPending;
    private ArmoryBannerStore _bannerStore = new();
    private AvatarFileSelectionService _bannerSelection = new(new BannerFilePicker());
    private ArmoryBannerData? _banner;
    private ArmoryBannerData? _bannerDraft;
    private string? _bannerData;
    private string? _bannerError;
    private uint? _sessionAccountId;
    private bool _bannerBusy;
    private CancellationTokenSource? _bannerChoiceLifetime;
    private bool _disposed;

    public ArmoryViewV2()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _browser?.CoreWebView2?.Resume();
                if (_browser is null) _ = OpenAsync();
                else PublishProfile();
            }
        };
    }

    public event EventHandler? CustomizeRequested;
    public event EventHandler? WindowDragRequested;
    public event EventHandler<ArmoryHeaderHoverEventArgs>? HeaderHoverChanged;
    public event EventHandler<ArmoryProfileSaveRequestedEventArgs>? ProfileSaveRequested;
    public event EventHandler<ArmoryAvatarRequestedEventArgs>? AvatarChangeRequested;
    public event EventHandler<ArmoryAvatarRequestedEventArgs>? AvatarRemoveRequested;
    internal bool IsConfigured => _getAccount is not null;
    internal WebView2CompositionControl? Browser => _browser;

    internal void Configure(Func<CancellationToken, Task<uint?>> getAccount, AccountUiState state,
        Func<LauncherArmoryLocalConfiguration>? loadConfiguration = null, string? userDataFolder = null,
        ArmoryBannerStore? bannerStore = null, IAvatarFilePicker? bannerPicker = null,
        Func<uint, LauncherArmoryDataRequest, CancellationToken, Task<JsonElement>>? readData = null,
        Func<string?>? getGameDirectory = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResetSession();
        if (_state is not null) _state.PropertyChanged -= StateChanged;
        LauncherLocalization.LocaleChanged -= LocaleChanged;
        _getAccount = getAccount;
        _state = state;
        _loadConfiguration = loadConfiguration ?? (() => LauncherArmoryLocalHost.LoadConfiguration(getGameDirectory?.Invoke()));
        _readData = readData;
        _userDataFolder = userDataFolder;
        _bannerStore = bannerStore ?? new ArmoryBannerStore();
        _bannerSelection = new AvatarFileSelectionService(bannerPicker ?? new BannerFilePicker());
        state.PropertyChanged += StateChanged;
        LauncherLocalization.LocaleChanged += LocaleChanged;
        if (IsVisible) _ = OpenAsync();
    }

    private async Task OpenAsync()
    {
        if (_disposed || _lifetime is not null || _getAccount is null || _state?.IsNavigationEnabled != true || !IsVisible) return;
        CancellationTokenSource lifetime = _lifetime = new CancellationTokenSource();
        CancellationToken token = lifetime.Token;
        _sessionUsername = _state.Current.Username;
        StatusPanel.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
        CustomizeButton.Content = LauncherLocalization.IsEnglish ? "Customize profile" : "Personnaliser le profil";
        StatusText.Text = LauncherLocalization.IsEnglish ? "Loading profile…" : "Chargement du profil…";
        try
        {
            uint? account = await _getAccount(token);
            token.ThrowIfCancellationRequested();
            if (account is null || _state?.IsNavigationEnabled != true) throw new InvalidOperationException("Session unavailable.");
            _sessionAccountId = account;
            try
            {
                ArmoryBannerData? banner = await _bannerStore.LoadAsync(account.Value, token);
                token.ThrowIfCancellationRequested();
                _banner = banner;
                _bannerData = banner?.DataUrl;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                token.ThrowIfCancellationRequested();
                _bannerError = LocalText("La bannière enregistrée sur cet appareil est indisponible.", "The banner saved on this device is unavailable.");
            }
            LauncherArmoryLocalHost host = _host = new LauncherArmoryLocalHost();
            LauncherArmoryLocalConfiguration configuration = await Task.Run(_loadConfiguration, token);
            token.ThrowIfCancellationRequested();
            await LauncherWebViewRuntime.EnsureAvailableAsync(configuration, token,
                () => StatusText.Text = LocalText("Installation du composant Microsoft WebView2…", "Installing the Microsoft WebView2 component…"));
            token.ThrowIfCancellationRequested();
            await host.StartAsync(account.Value, configuration, token,
                _readData is null ? null : (request, requestToken) => _readData(account.Value, request, requestToken));
            token.ThrowIfCancellationRequested();
            WebView2CompositionControl browser = _browser = new WebView2CompositionControl
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 16, 20, 24)
            };
            BrowserHost.Children.Add(browser);
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: _userDataFolder ?? Path.GetFullPath(Path.Combine(LauncherBuildFlavor.GetAvatarCacheRoot(), "..", "armory-webview")));
            token.ThrowIfCancellationRequested();
            await browser.EnsureCoreWebView2Async(environment);
            token.ThrowIfCancellationRequested();
            CoreWebView2 core = browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!token.IsCancellationRequested && host.Owns(args.Request.Uri)) args.Request.Headers.SetHeader("X-Atlas-Armory-Key", host.Key);
                else args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            core.NavigationStarting += (_, args) => args.Cancel = !host.Owns(args.Uri);
            core.FrameNavigationStarting += (_, args) => args.Cancel = args.Uri != "about:blank" && !host.Owns(args.Uri);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.WebMessageReceived += (_, args) =>
            {
                HandleProfileMessage(args.Source, args.WebMessageAsJson, host, token);
            };
            core.NavigationCompleted += (_, args) =>
            {
                if (token.IsCancellationRequested) return;
                StatusPanel.Visibility = args.IsSuccess ? Visibility.Collapsed : Visibility.Visible;
                if (args.IsSuccess) PublishProfile();
                else ShowFailure();
            };
            core.Navigate(new Uri(host.Origin!, "?lang=" + (LauncherLocalization.IsEnglish ? "en" : "fr")).AbsoluteUri);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (ReferenceEquals(_lifetime, lifetime)) { ResetSession(); ShowFailure(); }
        }
    }

    private void HandleProfileMessage(string source, string message, LauncherArmoryLocalHost host, CancellationToken token)
    {
        if (token.IsCancellationRequested || !ReferenceEquals(host, _host)
            || _state?.IsNavigationEnabled != true || _sessionUsername != _state.Current.Username
            || !host.Owns(source) || !Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) || uri.AbsolutePath != "/"
            || message.Length > 4096) return;
        try
        {
            using JsonDocument json = JsonDocument.Parse(message);
            JsonElement request = json.RootElement;
            if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("action", out JsonElement actionValue)
                || actionValue.ValueKind != JsonValueKind.String) return;
            string? action = actionValue.GetString();
            if (action == "ready") { PublishProfile(); return; }
            if (action == "customize") { OpenProfileEditor(); return; }
            AccountViewState state = _state.Current;
            bool connected = state.IsRuntimeConnected && !state.IsPreview && IsVisible;
            if (action == "profile-header-hover")
            {
                if (connected && request.TryGetProperty("hovered", out JsonElement hovered)
                    && hovered.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    HeaderHoverChanged?.Invoke(this, new ArmoryHeaderHoverEventArgs(hovered.GetBoolean()));
                return;
            }
            if (action == "drag-window")
            {
                if (connected) WindowDragRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (action is "choose-banner" or "save-banner" or "cancel-banner" or "reset-banner")
            {
                HandleBannerMessage(action, request, token, connected);
                return;
            }
            if (action == "save-profile")
            {
                _profileBridgeError = null;
                bool valid = request.TryGetProperty("statusMessage", out JsonElement status) && status.ValueKind == JsonValueKind.String
                    && request.TryGetProperty("bio", out JsonElement bio) && bio.ValueKind == JsonValueKind.String;
                string statusMessage = valid ? request.GetProperty("statusMessage").GetString()! : string.Empty;
                string biography = valid ? request.GetProperty("bio").GetString()! : string.Empty;
                valid = valid && statusMessage.Length <= 80 && biography.Length <= 280;
                ArmoryProfileSaveRequestedEventArgs save = new(statusMessage, biography);
                if (connected && state.CanUpdateSocialProfile && state.AccountOperation == AccountOperationViewState.None && valid)
                    ProfileSaveRequested?.Invoke(this, save);
                if (!save.Accepted) _profileBridgeError = valid
                    ? LocalText("Le profil ne peut pas être enregistré pour le moment.", "The profile cannot be saved right now.")
                    : LocalText("Le statut est limité à 80 caractères et la biographie à 280 caractères.", "Status is limited to 80 characters and biography to 280 characters.");
                _browser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "profile-save-result", accepted = save.Accepted, message = _profileBridgeError
                }));
                PublishProfile();
            }
            else if (action is "change-avatar" or "remove-avatar")
            {
                _avatarBridgeError = null;
                bool confirmed = request.TryGetProperty("confirmed", out JsonElement confirmation) && confirmation.ValueKind == JsonValueKind.True;
                ArmoryAvatarRequestedEventArgs avatar = new(token);
                if (connected && !_avatarSelectionPending && state.AvatarOperation == AvatarPreviewOperation.None)
                {
                    if (action == "change-avatar" && state.CanModifyAvatar)
                    {
                        _avatarSelectionPending = true;
                        AvatarChangeRequested?.Invoke(this, avatar);
                        if (!avatar.Accepted) _avatarSelectionPending = false;
                    }
                    else if (action == "remove-avatar" && confirmed && state.CanRemoveAvatar) AvatarRemoveRequested?.Invoke(this, avatar);
                }
                if (!avatar.Accepted) _avatarBridgeError = LocalText("La photo ne peut pas être modifiée pour le moment.", "The profile photo cannot be changed right now.");
                PublishProfile();
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or COMException)
        {
            // Malformed or stale messages must never invoke a different session's commands.
        }
    }

    private static string LocalText(string french, string english) => LauncherLocalization.IsEnglish ? english : french;

    private void HandleBannerMessage(string action, JsonElement request, CancellationToken token, bool connected)
    {
        if (action == "cancel-banner")
        {
            _bannerChoiceLifetime?.Cancel();
            _bannerDraft = null;
            _bannerError = null;
            PublishProfile();
            return;
        }
        if (!connected || _sessionAccountId is null || _bannerBusy)
        {
            string error = LocalText("La bannière ne peut pas être modifiée pour le moment.", "The banner cannot be changed right now.");
            if (action == "choose-banner")
                PostBannerMessage(new { type = "banner-selection-cancelled", error });
            else PostBannerResult(false, false, true, error);
            PublishProfile();
            return;
        }
        if (action == "choose-banner")
        {
            _ = ChooseBannerAsync(token);
            return;
        }
        bool reset = action == "reset-banner";
        bool valid = reset
            ? request.TryGetProperty("confirmed", out JsonElement confirmed) && confirmed.ValueKind == JsonValueKind.True
            : TryReadBannerPosition(request, "positionX", out _) && TryReadBannerPosition(request, "positionY", out _)
                && TryReadBannerZoom(request, out _) && TryReadBannerFit(request, out _) && !request.TryGetProperty("image", out _);
        if (!valid)
        {
            _bannerError = LocalText("Sélectionne une image et un cadrage valides, ou confirme la réinitialisation.",
                "Choose a valid image and focal point, or confirm the reset.");
            PostBannerResult(false, false, true, _bannerError);
            PublishProfile();
            return;
        }
        ArmoryBannerData? next = null;
        if (!reset)
        {
            TryReadBannerPosition(request, "positionX", out double x);
            TryReadBannerPosition(request, "positionY", out double y);
            TryReadBannerZoom(request, out double zoom);
            TryReadBannerFit(request, out string fit);
            next = (_bannerDraft ?? _banner ?? new ArmoryBannerData(null, 0.5, 0.3)) with { PositionX = x, PositionY = y, Zoom = zoom, Fit = fit };
        }
        _ = SaveBannerAsync(next, token);
    }

    private static bool TryReadBannerPosition(JsonElement request, string property, out double value)
    {
        value = 0;
        return request.TryGetProperty(property, out JsonElement position) && position.ValueKind == JsonValueKind.Number
            && position.TryGetDouble(out value) && double.IsFinite(value) && value is >= 0 and <= 1;
    }

    private static bool TryReadBannerZoom(JsonElement request, out double value)
    {
        value = 1;
        if (!request.TryGetProperty("zoom", out JsonElement zoom)) return true;
        return zoom.ValueKind == JsonValueKind.Number && zoom.TryGetDouble(out value)
            && double.IsFinite(value) && value is >= 1 and <= 3;
    }

    private static bool TryReadBannerFit(JsonElement request, out string value)
    {
        value = "contain";
        if (!request.TryGetProperty("fit", out JsonElement fit)) return true;
        if (fit.ValueKind != JsonValueKind.String || fit.GetString() is not ("contain" or "cover")) return false;
        value = fit.GetString()!;
        return true;
    }

    private bool IsBannerSessionCurrent(CancellationToken token) => !token.IsCancellationRequested
        && _lifetime?.Token == token && _state?.IsNavigationEnabled == true
        && _sessionUsername == _state.Current.Username;

    private async Task ChooseBannerAsync(CancellationToken sessionToken)
    {
        using CancellationTokenSource choice = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _bannerChoiceLifetime = choice;
        _bannerBusy = true;
        _bannerError = null;
        PublishProfile();
        try
        {
            AvatarPreviewImage? selected = await _bannerSelection.PickAndLoadAsync(choice.Token);
            choice.Token.ThrowIfCancellationRequested();
            if (!IsBannerSessionCurrent(sessionToken)) return;
            if (selected is null)
            {
                PostBannerMessage(new { type = "banner-selection-cancelled" });
                return;
            }
            byte[] png = await Task.Run(() => ArmoryBannerStore.Normalize(selected.OrientedImage), choice.Token);
            choice.Token.ThrowIfCancellationRequested();
            if (!IsBannerSessionCurrent(sessionToken)) return;
            _bannerDraft = new ArmoryBannerData(png, 0.5, 0.5);
            PostBannerMessage(new { type = "banner-selected", image = _bannerDraft.DataUrl, positionX = 0.5, positionY = 0.5, zoom = 1, fit = _bannerDraft.Fit });
        }
        catch (OperationCanceledException)
        {
            if (IsBannerSessionCurrent(sessionToken)) PostBannerMessage(new { type = "banner-selection-cancelled" });
        }
        catch (Exception)
        {
            if (IsBannerSessionCurrent(sessionToken) && !choice.IsCancellationRequested)
            {
                _bannerError = LocalText("Impossible d’ouvrir cette image. Choisis un fichier JPEG, PNG ou WebP valide.",
                    "This image could not be opened. Choose a valid JPEG, PNG or WebP file.");
                PostBannerMessage(new { type = "banner-selection-cancelled" });
            }
        }
        finally
        {
            if (ReferenceEquals(_bannerChoiceLifetime, choice)) _bannerChoiceLifetime = null;
            if (IsBannerSessionCurrent(sessionToken)) { _bannerBusy = false; PublishProfile(); }
        }
    }

    private async Task SaveBannerAsync(ArmoryBannerData? next, CancellationToken token)
    {
        uint accountId = _sessionAccountId!.Value;
        _bannerBusy = true;
        _bannerError = null;
        PostBannerResult(true, false, false, null);
        PublishProfile();
        try
        {
            if (next is null) await _bannerStore.ResetAsync(accountId, token);
            else await _bannerStore.SaveAsync(accountId, next, token);
            if (!IsBannerSessionCurrent(token)) return;
            _banner = next;
            _bannerData = next?.DataUrl;
            _bannerDraft = null;
            PostBannerResult(true, true, true, null);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (IsBannerSessionCurrent(token))
            {
                _bannerError = LocalText("La bannière n’a pas pu être enregistrée sur cet appareil. Réessaie.",
                    "The banner could not be saved on this device. Try again.");
                PostBannerResult(true, false, true, _bannerError);
            }
        }
        finally
        {
            if (IsBannerSessionCurrent(token)) { _bannerBusy = false; PublishProfile(); }
        }
    }

    private void PostBannerResult(bool accepted, bool succeeded, bool completed, string? error) =>
        PostBannerMessage(new { type = "banner-save-result", accepted, succeeded, completed, error });

    private void PostBannerMessage(object message)
    {
        try { _browser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception error) when (error is InvalidOperationException or COMException) { }
    }

    private sealed class BannerFilePicker : IAvatarFilePicker
    {
        public string? PickImagePath()
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = LocalText("Choisir une bannière", "Choose a banner"),
                Filter = "Images JPEG, PNG ou WebP|*.jpg;*.jpeg;*.png;*.webp",
                CheckFileExists = true,
                Multiselect = false
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }

    internal void OpenProfileEditor()
    {
        if (_browser?.CoreWebView2 is { } core && _state?.IsNavigationEnabled == true)
            core.PostWebMessageAsJson("{\"type\":\"profile-editor-open\"}");
    }

    internal void CompleteAvatarSelection(CancellationToken sessionToken)
    {
        if (_lifetime is null || _lifetime.Token != sessionToken || sessionToken.IsCancellationRequested) return;
        _avatarSelectionPending = false;
        _avatarBridgeError = null;
        PublishProfile();
    }

    private void ShowFailure()
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = LauncherLocalization.IsEnglish ? "Armory unavailable" : "Armurerie indisponible";
        RetryButton.Content = LauncherLocalization.IsEnglish ? "Retry" : "Réessayer";
        RetryButton.Visibility = Visibility.Visible;
    }

    private void PublishProfile()
    {
        if (_browser?.CoreWebView2 is not { } core || _state?.IsNavigationEnabled != true) return;
        AccountViewState state = _state.Current;
        if (!ReferenceEquals(_avatarSource, state.AvatarImage))
        {
            _avatarSource = state.AvatarImage;
            _avatarData = EncodeAvatar(state.AvatarImage);
        }
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "profile", username = state.Username,
                statusMessage = state.StatusMessage, bio = state.Bio, avatar = _avatarData,
                canUpdateSocialProfile = state.IsRuntimeConnected && !state.IsPreview && state.CanUpdateSocialProfile,
                canModifyAvatar = state.IsRuntimeConnected && !state.IsPreview && !_avatarSelectionPending && state.CanModifyAvatar,
                canRemoveAvatar = state.IsRuntimeConnected && !state.IsPreview && !_avatarSelectionPending && state.CanRemoveAvatar,
                profileBusy = state.AccountOperation == AccountOperationViewState.UpdatingProfile,
                profileError = _profileBridgeError ?? (state.AccountErrorOperation == AccountOperationViewState.UpdatingProfile ? state.AccountErrorMessage : string.Empty),
                profileNotice = state.AccountNotice == AccountNoticeViewState.ProfileUpdated ? state.AccountNoticeMessage : string.Empty,
                avatarBusy = _avatarSelectionPending || state.AvatarOperation != AvatarPreviewOperation.None,
                avatarError = _avatarBridgeError ?? state.AvatarErrorMessage,
                avatarNotice = state.AvatarStatusMessage,
                banner = _bannerData,
                bannerPositionX = _banner?.PositionX ?? 0.5,
                bannerPositionY = _banner?.PositionY ?? 0.3,
                bannerZoom = _banner?.Zoom ?? 1,
                bannerFit = _banner?.Fit ?? "contain",
                hasBannerCustomization = _banner is not null,
                canModifyBanner = state.IsRuntimeConnected && !state.IsPreview && _sessionAccountId is not null && !_bannerBusy,
                bannerBusy = _bannerBusy,
                bannerError = _bannerError,
                locale = LauncherLocalization.IsEnglish ? "en" : "fr" }));
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            ResetSession();
            ShowFailure();
        }
    }

    private static string? EncodeAvatar(BitmapSource? image)
    {
        if (image is null) return null;
        try
        {
            // Frozen frames can still hold decoder metadata owned by a worker thread.
            // Copy just the pixels into a new source before creating an encoder frame.
            int width = image.PixelWidth, height = image.PixelHeight;
            if (width < 1 || height < 1 || width > 4096 || height > 4096) return null;
            int stride = checked((width * image.Format.BitsPerPixel + 7) / 8);
            byte[] pixels = new byte[checked(stride * height)];
            image.CopyPixels(pixels, stride, 0);
            BitmapSource detached = BitmapSource.Create(width, height, 96, 96, image.Format, image.Palette, pixels, stride);
            double scale = Math.Min(1, 256.0 / Math.Max(width, height));
            BitmapSource sized = scale < 1 ? new TransformedBitmap(detached, new ScaleTransform(scale, scale)) : detached;
            using MemoryStream stream = new();
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(sized));
            encoder.Save(stream);
            return stream.Length < 1_400_000 ? "data:image/png;base64," + Convert.ToBase64String(stream.ToArray()) : null;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException
            or ArgumentException or IOException or OverflowException or COMException)
        {
            // A bad avatar must not suppress the account identity or break navigation.
            return null;
        }
    }

    private void StateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_state?.IsNavigationEnabled != true) { ResetSession(); return; }
        if (_sessionUsername is not null && _sessionUsername != _state.Current.Username) ResetSession();
        if (_lifetime is null && IsVisible) _ = OpenAsync();
        else PublishProfile();
    }

    private void LocaleChanged(object? sender, EventArgs args)
    {
        CustomizeButton.Content = LauncherLocalization.IsEnglish ? "Customize profile" : "Personnaliser le profil";
        if (RetryButton.Visibility == Visibility.Visible) ShowFailure();
        else StatusText.Text = LauncherLocalization.IsEnglish ? "Loading profile…" : "Chargement du profil…";
        PublishProfile();
    }
    private void RetryButton_Click(object sender, RoutedEventArgs args) { ResetSession(); _ = OpenAsync(); }
    private void CustomizeButton_Click(object sender, RoutedEventArgs args) => CustomizeRequested?.Invoke(this, EventArgs.Empty);

    internal void ResetSession()
    {
        CancellationTokenSource? lifetime = _lifetime;
        _lifetime = null;
        _sessionUsername = null;
        _avatarSource = null;
        _avatarData = null;
        _profileBridgeError = null;
        _avatarBridgeError = null;
        _avatarSelectionPending = false;
        _bannerChoiceLifetime?.Cancel();
        _bannerChoiceLifetime = null;
        _banner = null;
        _bannerDraft = null;
        _bannerData = null;
        _bannerError = null;
        _bannerBusy = false;
        _sessionAccountId = null;
        lifetime?.Cancel();
        _browser?.Dispose(); _browser = null; BrowserHost.Children.Clear();
        _host?.Dispose(); _host = null;
        lifetime?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetSession();
        if (_state is not null) _state.PropertyChanged -= StateChanged;
        LauncherLocalization.LocaleChanged -= LocaleChanged;
        _getAccount = null; _state = null;
    }
}
