using System.Globalization;
using System.Windows.Threading;
using WotLK.Launcher.Dashboard;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed class DashboardStateAdapter : IDisposable
{
    private static readonly CultureInfo FrenchCulture = CultureInfo.GetCultureInfo("fr-FR");
    private readonly DashboardUiState _target;
    private readonly ILauncherDashboardRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private long _latestSequence = -1;
    private int _disposeState;

    internal DashboardStateAdapter(
        DashboardUiState target,
        ILauncherDashboardRuntime runtime,
        Dispatcher dispatcher)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.SnapshotChanged += Runtime_SnapshotChanged;
        ApplyOrQueue(_runtime.CurrentSnapshot);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _runtime.SnapshotChanged -= Runtime_SnapshotChanged;
        }
    }

    internal static DashboardViewState Project(DashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string compactLabel = GetRealmLabel(snapshot.RealmState);
        string wideLabel = snapshot.RealmState == DashboardRealmState.Online
            ? "Arthas en ligne"
            : compactLabel;
        string patchTitle = snapshot.HasPatchNote
            ? EmptyFallback(snapshot.LatestPatchNoteTitle, "Note de mise à jour")
            : "Aucune note de mise à jour disponible.";
        string patchSummary = snapshot.HasPatchNote
            ? EmptyFallback(snapshot.LatestPatchNoteSummary, "Aucun résumé disponible.")
            : string.Empty;
        string metaText = snapshot.LatestPatchNoteDate is DateTimeOffset date
            ? date.ToLocalTime().ToString("dd MMMM yyyy", FrenchCulture)
            : string.Empty;
        if (snapshot.IsStale)
        {
            metaText = string.IsNullOrWhiteSpace(metaText)
                ? "Données conservées après un échec d’actualisation"
                : $"{metaText} · données conservées";
        }
        else if (snapshot.FailureCategory != DashboardFailureCategory.None
                 && !snapshot.HasPatchNote)
        {
            metaText = "Actualisation indisponible";
        }

        return new DashboardViewState(
            snapshot.RealmState,
            compactLabel,
            wideLabel,
            BuildRealmToolTip(snapshot),
            snapshot.IsLoading,
            snapshot.HasPatchNote ? snapshot.LatestPatchNoteVersion : string.Empty,
            patchTitle,
            patchSummary,
            metaText,
            snapshot.HasPatchNote,
            snapshot.IsStale,
            CanOpenLatestPatchNote: false);
    }

    private void Runtime_SnapshotChanged(
        object? sender,
        DashboardSnapshotEventArgs eventArgs)
    {
        ApplyOrQueue(eventArgs.Snapshot);
    }

    private void ApplyOrQueue(DashboardSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Apply(snapshot);
            return;
        }

        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => Apply(snapshot)));
    }

    private void Apply(DashboardSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0 || snapshot.Sequence <= _latestSequence)
        {
            return;
        }

        DashboardViewState state = Project(snapshot);
        _latestSequence = snapshot.Sequence;
        _target.ApplyView(state);
    }

    private static string GetRealmLabel(DashboardRealmState state)
    {
        return state switch
        {
            DashboardRealmState.Online => "En ligne",
            DashboardRealmState.Degraded => "Services dégradés",
            DashboardRealmState.Offline => "Hors ligne",
            DashboardRealmState.Loading => "Actualisation…",
            DashboardRealmState.Unavailable => "Statut indisponible",
            _ => "Non vérifié"
        };
    }

    private static string BuildRealmToolTip(DashboardSnapshot snapshot)
    {
        if (snapshot.IsLoading)
        {
            return "Actualisation du statut et de la note de mise à jour en cours.";
        }

        if (snapshot.IsStale && snapshot.LastSuccessfulRefreshAt is DateTimeOffset staleAt)
        {
            return $"Dernière actualisation réussie à {staleAt.ToLocalTime():HH:mm:ss}. "
                + "Les données affichées peuvent être anciennes.";
        }

        if (snapshot.LastSuccessfulRefreshAt is DateTimeOffset refreshedAt)
        {
            return $"Dernière actualisation réussie à {refreshedAt.ToLocalTime():HH:mm:ss}.";
        }

        return snapshot.RealmState == DashboardRealmState.Unavailable
            ? "Le statut du royaume est actuellement indisponible."
            : "Actualiser le statut du royaume et la note de mise à jour.";
    }

    private static string EmptyFallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
