using System.Windows.Input;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Commands;

internal sealed class GameCommands : IDisposable
{
    private readonly ILauncherLocalActions _localActions;
    private readonly Action<LauncherLocalActionResult> _publishResult;
    private readonly DelegateCommand _openGameFolder;
    private readonly DelegateCommand _openDiagnostic;
    private int _disposeState;

    internal GameCommands(
        ILauncherLocalActions localActions,
        Action<LauncherLocalActionResult> publishResult)
    {
        _localActions = localActions ?? throw new ArgumentNullException(nameof(localActions));
        _publishResult = publishResult ?? throw new ArgumentNullException(nameof(publishResult));
        _openGameFolder = new DelegateCommand(
            ExecuteOpenGameFolder,
            () => _localActions.CanOpenGameFolder);
        _openDiagnostic = new DelegateCommand(
            ExecuteOpenDiagnostic,
            () => _localActions.CanOpenDiagnostic);
        _localActions.AvailabilityChanged += LocalActions_AvailabilityChanged;
    }

    internal ICommand OpenGameFolder => _openGameFolder;

    internal ICommand OpenDiagnostic => _openDiagnostic;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _localActions.AvailabilityChanged -= LocalActions_AvailabilityChanged;
        _openGameFolder.Dispose();
        _openDiagnostic.Dispose();
    }

    private void ExecuteOpenGameFolder()
    {
        _publishResult(_localActions.OpenGameFolder());
    }

    private void ExecuteOpenDiagnostic()
    {
        _publishResult(_localActions.OpenDiagnostic());
    }

    private void LocalActions_AvailabilityChanged(object? sender, EventArgs e)
    {
        _openGameFolder.RaiseCanExecuteChanged();
        _openDiagnostic.RaiseCanExecuteChanged();
    }
}
