namespace WotLK.Launcher.UI.V2.Presentation;

public sealed class PatchNoteUiState : BindableUiState
{
    private bool _isOpen;

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }
}
