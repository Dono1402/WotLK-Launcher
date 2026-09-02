using System.Collections.Immutable;

namespace WotLK.Launcher.UI.V2.Presentation;

public enum ActivityRecentOutcome
{
    Succeeded,
    Failed,
    Cancelled
}

public enum ActivityNavigationTarget
{
    None,
    Game,
    Addons
}

public sealed record ActivityOperationUiItem(
    string ProductName,
    string ActionName,
    string PhaseText,
    double? ProgressPercent,
    bool IsIndeterminate,
    string TransferText,
    string RateAndEtaText,
    string DetailText,
    string IconUri,
    bool HasIcon,
    bool CanUserCancel,
    bool IsCancellationRequested,
    string ErrorMessage,
    string BatchPosition)
{
    public bool IsDeterminate => ProgressPercent is not null && !IsIndeterminate;

    public bool HasTransferText => !string.IsNullOrWhiteSpace(TransferText);

    public bool HasPhaseText => !string.IsNullOrWhiteSpace(PhaseText);

    public bool HasRateAndEtaText => !string.IsNullOrWhiteSpace(RateAndEtaText);

    public bool HasDetailText => !string.IsNullOrWhiteSpace(DetailText);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasBatchPosition => !string.IsNullOrWhiteSpace(BatchPosition);

    public bool ShowsCancelAction => CanUserCancel || IsCancellationRequested;

    public string CancelActionLabel => IsCancellationRequested ? "Annulation…" : "Annuler";

    public string PercentText => ProgressPercent is double percent
        ? $"{percent:0} %"
        : string.Empty;
}

public sealed record ActivityPendingUiItem(
    string ProductName,
    string ActionName,
    string IconUri,
    bool HasIcon);

public sealed record ActivityRecentUiItem(
    string ProductName,
    string ResultText,
    string CompletedAtText,
    ActivityRecentOutcome Outcome,
    ActivityNavigationTarget NavigationTarget,
    string IconUri,
    bool HasIcon)
{
    public bool CanNavigate => NavigationTarget != ActivityNavigationTarget.None;
}

public sealed record ActivityViewState(
    bool IsPreview,
    ActivityOperationUiItem? ActiveOperation,
    ImmutableArray<ActivityPendingUiItem> PendingOperations,
    ImmutableArray<ActivityRecentUiItem> RecentOperations)
{
    public bool HasActiveOperation => ActiveOperation is not null;

    public bool HasPendingOperations => !PendingOperations.IsDefaultOrEmpty;

    public bool HasRecentOperations => !RecentOperations.IsDefaultOrEmpty;

    public bool ShowsEmptyState => !HasActiveOperation && !HasPendingOperations && !HasRecentOperations;

    public bool TopBarShowsPercent => ActiveOperation is
        { IsDeterminate: true, HasBatchPosition: false };

    public string TopBarPercentText => TopBarShowsPercent
        ? ActiveOperation!.PercentText
        : string.Empty;

    public bool TopBarIsIndeterminate => ActiveOperation?.IsIndeterminate == true;

    public bool HasRecentFailure => RecentOperations.Any(item => item.Outcome == ActivityRecentOutcome.Failed)
        || ActiveOperation?.HasError == true;
}

public sealed class ActivityUiState : BindableUiState
{
    private ActivityViewState _current;
    private bool _isOpen;

    internal ActivityUiState(ActivityViewState? current = null)
    {
        _current = current ?? EmptyView;
    }

    public static ActivityViewState EmptyView { get; } = new(
        IsPreview: false,
        ActiveOperation: null,
        PendingOperations: ImmutableArray<ActivityPendingUiItem>.Empty,
        RecentOperations: ImmutableArray<ActivityRecentUiItem>.Empty);

    public ActivityViewState Current => _current;

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    internal bool RequestPreviewCancellation()
    {
        ActivityOperationUiItem? active = _current.ActiveOperation;
        if (!_current.IsPreview || active is null || !active.CanUserCancel)
        {
            return false;
        }

        _current = _current with
        {
            ActiveOperation = active with
            {
                ActionName = "Annulation…",
                PhaseText = "Arrêt de l’opération en cours…",
                CanUserCancel = false,
                IsCancellationRequested = true
            }
        };
        RaisePropertyChanged(string.Empty);
        return true;
    }
}
