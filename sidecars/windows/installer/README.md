# Windows installer preview

Build from a Windows checkout with .NET 10 SDK, PowerShell 7 and Python 3:

```powershell
./sidecars/windows/installer/build.ps1
```

The script publishes the worker and WPF tray companion, bundles private Microsoft .NET and
Windows Desktop runtimes 10.0.12, verifies pinned download hashes, fetches the existing pinned
FFmpeg bundle, generates stable file components, and builds an MSI with WiX 4.0.6. No global
runtime or WiX installation is changed. The MSI and SHA-256 sidecar appear in
`sidecars/windows/artifacts/`. Installation is offline once the MSI has been built.

Open **Optimisarr Sidecar** from Start after installation, then click its notification-area icon.
Preferences offers **Pair this PC**, **Start worker**, and the per-user **Open tray at sign-in**
setting. The service starts with Windows independently of the tray. First installation leaves
it stopped until pairing is complete. Windows administrator approval applies to pairing/service
start, not ordinary viewing or pausing.

The tray executable locates the private runtime using .NET's
[app-relative runtime discovery](https://learn.microsoft.com/en-us/dotnet/core/deploying/).
The service runs through the private Microsoft-signed `dotnet.exe`; its WiX component uses that
file as its [service executable key path](https://docs.firegiant.com/wix/schema/wxs/serviceinstall/).

## Upgrade and data handling

The installer refuses to replace a manually registered `OptimisarrSidecar` service. Drain that
worker in the server UI, wait for its job to finish, then explicitly remove the old service
registration before installing. Keep its pairing file if the same server/worker should be retained.
This safeguard also avoids overwriting a separately managed developer installation.

MSI upgrades stop the worker and replace program files. Development previews permit upgrades
with the same version so a corrected preview can replace the previous build; lower version
numbers are blocked. Keep the previous MSI if a preview needs to be restored. After an upgrade,
use **Start worker** (or reboot). Do not upgrade in the middle of work unless handing that job
back is acceptable. Uninstall removes program files and service registration but retains
`%ProgramData%\Optimisarr\Sidecar` and scratch/media data. Pairing storage is restricted to
Administrators and SYSTEM. Per-user preferences, including the sign-in setting, are user-owned;
turn off sign-in before uninstalling if desired.

After uninstalling, an administrator can permanently remove retained pairing, scratch and media
data by deleting `%ProgramData%\Optimisarr\Sidecar`. Do this only when the worker will not be
reinstalled: deleting the pairing prevents it from reconnecting until it is paired again, and
deleting work data cannot be undone.

## Validation

The Windows build passes WiX package validation and supports administrative extraction. Native
pipe tests exercise read/pause/resume, ACL rules and invalid requests. The installed tray uses the
private runtime, renders isolated native fixture states, and checks real window anchoring across
page changes and disclosure expansion/collapse.

For actual installation/uninstallation, use a disposable elevated Windows VM with no existing
sidecar or pairing directory:

```powershell
./sidecars/windows/installer/test-install.ps1 -Installer ./sidecars/windows/artifacts/OptimisarrSidecar-0.2.13-win-x64.msi
```

The test refuses an existing sidecar, installs silently, checks registration and private runtimes,
renders the installed UI, checks native popover anchoring, then uninstalls and verifies retained data. The
`Windows sidecar installer` GitHub workflow builds and runs this test on a fresh Windows
runner for relevant pull requests and manual dispatches. The live migration from a manually
registered service to MSI was tested on a paired Windows PC: installation succeeded, retained
the original pairing, and the installed service checked in with the server. A same-version preview upgrade was also exercised on that PC, preserving pairing and passing
the installed native anchoring check. Interactive UAC pairing remains a separate manual check.

## Distribution status

This MSI and tray apphost are **unsigned development artifacts**. Smart App Control or enterprise
policy may reject an unsigned executable; do not weaken those protections. Public distribution
as a signed installer must follow the public [Code signing policy](../../../CODE_SIGNING_POLICY.md).
The SignPath Foundation application and production workflow are not complete; this link does not
mean the current download is signed. An explicitly labelled unsigned preview may
be published only after its exact corresponding-source bundle/hosting for the pinned GPL FFmpeg
build is available. Upstream source/build links and runtime licences are included in the package;
those links alone do not establish that the corresponding sources have been supplied.
The installer does not change the server’s worker-placement or strict verification policy.

The [installer workflow](../../../.github/workflows/windows-installer.yml) uploads CI artifacts.
On a reviewed `vX.Y.Z` tag it also attaches the tested MSI and checksum to the matching draft release.
Publish the coordinated release only after container and both native package gates pass and matching
source archives are available. A tag must match `Directory.Build.props`, which supplies the default
MSI version. Use `build.ps1 -Version <version>` for an explicit local build and follow the repository
[release checklist](../../../docs/development/releasing.md).
