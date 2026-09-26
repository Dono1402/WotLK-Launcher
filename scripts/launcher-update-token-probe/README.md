# Native Windows update relaunch regression

Build `Probe.csproj` with the .NET 8 SDK. From an **unelevated** PowerShell,
start the compiled WinExe with `coordinate "<new-result-directory>"`, using
`Start-Process -WindowStyle Hidden -PassThru` and wait for that process to exit.
An administrator UAC prompt may appear for its headless helper. Read
`report.json`; a zero exit code and `passed: true` are required.

The probe links the production `LauncherUpdateUserOperations.cs`. It captures
a disposable unelevated requester, lets that requester exit, and launches only
the probe itself through the actual elevated updater method. It verifies that
the child retains the requester's SID and session and is not elevated. It never
launches or changes the installed launcher, its settings, registry or services.

On the affected Windows machine, the pre-fix code reproduces Win32 error 5.
Duplicating with the original access mask, or adding only ADJUST_DEFAULT, still
fails. Adding both ADJUST_DEFAULT and ADJUST_SESSIONID succeeds. The fixed
production method passes this probe after the requester has exited.

The same-account UAC path was run on 2026-09-26. A prompt using a different
administrator account has not been exercised; the code continues to use the
captured requester token rather than the helper or Explorer token.
