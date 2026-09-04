# Atlas Launcher Windows Setup

`WotLK.Launcher.Installer` is the offline, per-machine x64 setup for Atlas
Launcher. Its stable distribution filename is `AtlasLauncherSetup.exe`.

## Product contract

- Product: **Atlas Launcher**
- Bootstrap payload: **1.3.0**
- Payload size and SHA-256: generated from the supplied canonical launcher during setup compilation
- Technical launcher filename: `WotLK.Launcher.exe`
- Default destination: `%ProgramFiles%\Atlas Launcher`
- Publisher: **AnimeClub**
- Uninstall key: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AtlasLauncher`

Setup never reads the production update manifest and never uses the network. The
embedded launcher owns all later signed HTTPS self-updates.

## Runtime boundary

The parameterless `InstallerWizardWindow` is the real setup. Its preview
constructor continues to use only deterministic local state and is hosted by the
IntegrationTests executable without UAC. Preview scenarios do not compose the
registry, shortcuts, process launch, payload extraction, or folder picker.

The distribution manifest uses `requireAdministrator`, so the complete setup is
elevated once. Atlas Launcher itself is never marked as elevated. At completion,
the setup delegates launch to the Explorer-hosted shell and therefore creates a
Medium-integrity process with the installation directory as its working folder.

## Transaction

`InstallerEngine` performs one single-flight transaction:

1. detect and block any registered or on-disk legacy launcher;
2. validate destination, access, drive type, and free space;
3. create a staging directory beside the destination;
4. stream the embedded payload to a partial file while measuring bytes and
   computing SHA-256;
5. copy the setup as `Uninstall.exe` and write the install state;
6. atomically move staging into the destination;
7. create selected shortcuts;
8. write the x64 HKLM uninstall registration last;
9. finalize the log and state.

On failure, the registration, owned shortcuts, committed destination, staging,
and empty parent directories created by that attempt are rolled back. A
pre-existing empty destination is restored as an empty directory.

Technical details are appended to
`%LocalAppData%\Atlas Launcher\Installer\install.log`; token/password/secret and
Bearer-shaped values are redacted.

## Uninstaller size decision

The installed `Uninstall.exe` is an exact copy of the standalone setup. This
keeps uninstall independent of an installed .NET runtime and avoids a fifth
project, but it duplicates the full setup size inside the installation. For the
1.3.0 release candidate sizes and hashes are recorded in the release report
generated from the final committed sources.

The uninstaller asks for confirmation in interactive mode and supports a tested
`--quiet` mode used by `QuietUninstallString`. It only removes the files listed by
the validated install state, owned shortcuts, and the matching x64 uninstall
key. It refuses to continue while the exact installed launcher path is running.
It never targets WoW, addons, `Config.wtf`, `.atlas-addons.json`, or Atlas data in
LocalAppData. A hidden post-exit PowerShell command removes `Uninstall.exe` and
the now-empty installation directory.

## Build

From the repository root:

```powershell
& '.\scripts\build-atlas-installer.ps1' `
  -LauncherPayloadPath '.\artifacts\release-v1.3.0\package\WotLK-Launcher.exe'
```

The script uses `C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe` explicitly,
validates and copies the canonical launcher payload without rebuilding it, generates the
embedded size and SHA-256 metadata from those exact bytes, and leaves only
`artifacts\AtlasLauncherSetup\AtlasLauncherSetup.exe` in the distribution
directory.

## Test entry points

```powershell
& 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe' run --project 'source\WotLK.Launcher.IntegrationTests\WotLK.Launcher.IntegrationTests.csproj' -c Release --no-build -- --installer-preview
& 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe' run --project 'source\WotLK.Launcher.IntegrationTests\WotLK.Launcher.IntegrationTests.csproj' -c Release --no-build -- --installer-runtime
& 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe' run --project 'source\WotLK.Launcher.IntegrationTests\WotLK.Launcher.IntegrationTests.csproj' -c Release --no-build -- --installer-runtime-artifact 'artifacts\AtlasLauncherSetup\AtlasLauncherSetup.exe'
```

The elevated Program Files/HKLM suite uses unique `Atlas Launcher 04D2 Test`
paths and registry keys. It snapshots the existing WotLK launcher, LocalAppData,
and WoW paths before and after every run. From an Administrator PowerShell:

```powershell
$result = 'artifacts\installer-04d2-elevated-result.txt'
& 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe' run --project 'source\WotLK.Launcher.IntegrationTests\WotLK.Launcher.IntegrationTests.csproj' -c Release --no-build -- --installer-runtime-elevated 'artifacts\AtlasLauncherSetup\AtlasLauncherSetup.exe' $result
Get-Content -LiteralPath $result
```
