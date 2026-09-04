using System.Collections.Immutable;
using System.Windows.Input;
using WotLK.Launcher.Dashboard;
using WotLK.Launcher.UI.V2.Commands;

namespace WotLK.Launcher.UI.V2.Presentation;

public sealed record PatchNoteSectionViewState(
    string Title,
    ImmutableArray<string> Items);

public sealed record PatchNoteEntryViewState(
    string Id,
    string Version,
    string Title,
    string PublishedText,
    string Intro,
    bool HasIntro,
    bool IsLatest,
    bool IsDraft,
    ImmutableArray<PatchNoteSectionViewState> Sections);

public sealed record DashboardViewState(
    DashboardRealmState RealmState,
    string RealmStatusLabel,
    string RealmStatusWideLabel,
    string RealmToolTip,
    bool IsLoading,
    string LatestPatchNoteVersion,
    string LatestPatchNoteTitle,
    string LatestPatchNoteSummary,
    string LatestPatchNoteMetaText,
    bool HasPatchNote,
    bool IsStale,
    bool CanOpenLatestPatchNote,
    ImmutableArray<PatchNoteEntryViewState> PatchNotes)
{
    internal static DashboardViewState Initial { get; } = new(
        RealmState: DashboardRealmState.Unknown,
        RealmStatusLabel: "Non vérifié",
        RealmStatusWideLabel: "Arthas non vérifié",
        RealmToolTip: "Le statut du royaume n’a pas encore été vérifié.",
        IsLoading: false,
        LatestPatchNoteVersion: string.Empty,
        LatestPatchNoteTitle: "Aucune note de mise à jour disponible.",
        LatestPatchNoteSummary: string.Empty,
        LatestPatchNoteMetaText: string.Empty,
        HasPatchNote: false,
        IsStale: false,
        CanOpenLatestPatchNote: false,
        PatchNotes: ImmutableArray<PatchNoteEntryViewState>.Empty);
}

public sealed class DashboardUiState : BindableUiState
{
    private DashboardViewState _current = DashboardViewState.Initial;
    private bool _useWideRealmLabel = true;

    public DashboardViewState Current => _current;

    public string DisplayRealmStatus => _useWideRealmLabel
        ? _current.RealmStatusWideLabel
        : _current.RealmStatusLabel;

    public ICommand RefreshCommand { get; private set; } = DisabledCommand.Instance;

    internal void ApplyView(DashboardViewState state)
    {
        _current = state ?? throw new ArgumentNullException(nameof(state));
        RaisePropertyChanged(string.Empty);
    }

    internal void SetWideRealmLabel(bool useWideRealmLabel)
    {
        if (_useWideRealmLabel == useWideRealmLabel)
        {
            return;
        }

        _useWideRealmLabel = useWideRealmLabel;
        RaisePropertyChanged(nameof(DisplayRealmStatus));
    }

    internal void AttachRefreshCommand(ICommand command)
    {
        RefreshCommand = command ?? throw new ArgumentNullException(nameof(command));
        RaisePropertyChanged(nameof(RefreshCommand));
    }
}
