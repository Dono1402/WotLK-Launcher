# Atlas Launcher Setup Wizard

This checkpoint introduces only the visual WPF wizard and its isolated preview
state. `App.xaml.cs`, `MainWindow`, `InstallerServices`, the application manifest,
the embedded launcher payload, and the existing install/uninstall behavior are
unchanged.

## Preview boundary

- `InstallerWizardWindow` consumes only `InstallerWizardUiState`.
- Every preview transition is local and deterministic.
- No preview type references `InstallerServices`, `Process`, the registry,
  shortcuts, the network, or the file system.
- The IntegrationTests process hosts the WPF window without elevation and saves
  captures under the ignored `artifacts/` directory.
- The legacy installer remains the default entry point until the functional
  installer checkpoint is explicitly approved.
- Manrope is linked from the launcher assets under SIL OFL 1.1; the pinned source,
  hashes, and licence are documented in `../WotLK.Launcher/Assets/ATTRIBUTIONS.md`.
- The Atlas logo and Icecrown image reuse the existing product-owned launcher
  assets. No preview-only generated replacement is introduced.

## Product and target

- Product: **Atlas Launcher**
- Bootstrap payload: **1.1.2**
- Embedded payload SHA-256: `690f0afed2010affef628115f6602815d9017e20189224300b79e3885c7ab2b6`
- Stable future distribution filename: `AtlasLauncherSetup.exe`
- Published PE architecture verified for launcher and installer: **x64**
- Default destination: `%ProgramFiles%\Atlas Launcher`

The x86 Program Files directory must only be selected by a genuinely x86
distribution. The destination policy will accept absolute paths on local fixed
drives, including spaces and accented characters. It will reject relative paths,
UNC/network paths, drive roots, Windows directories, the WoW client directory,
invalid paths, and locations that cannot be prepared safely.

## Functional composition planned after visual approval

The existing installer project remains the only installer executable. The future
composition will separate the unelevated wizard from a narrowly scoped elevated
apply mode in the same signed binary:

1. The normal wizard runs at user integrity and builds an immutable install plan.
2. Starting installation launches the same setup binary with `runas` and a
   validated, local plan identifier.
3. The elevated apply mode stages files beside the destination, verifies the
   embedded launcher payload, then commits files, shortcuts, and registry state.
4. Any failure rolls back only artifacts created by that attempt and writes
   technical details to `%LocalAppData%\Atlas Launcher\Installer\install.log`.
5. Completion is returned to the unelevated wizard. Launching Atlas Launcher is
   delegated through the interactive Explorer shell so it cannot inherit the
   administrator token.

There is no download during installation and no dependency on the production
update manifest. The embedded 1.1.2 launcher owns all later signed HTTPS updates.

## Windows registration

The functional checkpoint will use the 64-bit HKLM uninstall view for this x64
package and a stable Atlas-specific key. It will register `DisplayName`,
`DisplayVersion`, `Publisher` (`AnimeClub`), `InstallLocation`, `DisplayIcon`,
`UninstallString`, `InstallDate`, `EstimatedSize`, `NoModify`, and `NoRepair`.
`QuietUninstallString` will only be retained if the quiet flow receives dedicated
tests.

The installed `Uninstall.exe` will remove the launcher files, optional shortcuts,
its own uninstall entry, and itself. It must preserve WoW, addons, `Config.wtf`,
`.atlas-addons.json`, and all Atlas user data under LocalAppData.

## Progress and rollback

The functional progress model will report measured payload bytes plus completed
commit phases: preparation, destination creation, file installation, shortcuts,
Windows registration, and finalization. It will never advance from a timer.
Navigation and cancellation remain disabled while an atomic phase cannot be
rolled back safely.

Existing installations are only detected and blocked. No legacy files are read,
adopted, upgraded, repaired, or removed by the new setup flow.

## Manual preview

The IntegrationTests host opens the real WPF wizard without UAC or system-side
installation effects:

```powershell
& 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe' run --project 'source\WotLK.Launcher.IntegrationTests\WotLK.Launcher.IntegrationTests.csproj' -c Release --no-build -- --installer-preview-show welcome
```

Replace `welcome` with `destination`, `options`, `ready`, `installing`,
`completed`, `invalid-path`, `insufficient-space`, `existing-installation`, or
`install-error`. `F12` is a harness-only escape hatch, including from the locked
critical-installation preview.
