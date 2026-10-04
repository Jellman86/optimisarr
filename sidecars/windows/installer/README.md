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
numbers are blocked. Keep the previous MSI if a preview needs to be restored. An upgrade starts
the worker again when its retained `pairing.dat` exists. Fresh installations and unpaired upgrades
stay stopped until pairing. Drain the worker and wait for its jobs before upgrading: an upgrade
restarts even a previously stopped paired service. Do not upgrade in the middle of work unless handing that job
back is acceptable. Uninstall removes program files and service registration but retains
`%ProgramData%\Optimisarr\Sidecar` and scratch/media data. Pairing storage is restricted to
Administrators and SYSTEM. Per-user preferences, including the sign-in setting, are user-owned;
turn off sign-in before uninstalling if desired.

After uninstalling, an administrator can permanently remove retained pairing, scratch and media
data by deleting `%ProgramData%\Optimisarr\Sidecar`. Do this only when the worker will not be
reinstalled: deleting the pairing prevents it from reconnecting until it is paired again, and
deleting work data cannot be undone.

### Checked updates and recovery

Use the checked update route for an existing paired MSI installation. An MSI success message
confirms file installation; it does not prove that Windows permits the worker and tray to keep
running. The checked route requires PowerShell 7.4 or newer in a **local administrator** window.
The local monitor deliberately denies network logins, including an SSH session.

1. In the server's **Settings → Remote workers**, drain this PC and wait for its jobs to finish.
   Keep it drained throughout the update and recovery checks. Cancel any armed shutdown first.
2. Keep the exact previous MSI and its published SHA-256 checksum. The script verifies that its
   complete payload and MSI product match the current installation before changing anything.
3. Download the new MSI, its checksum and the matching `*-update.zip`. Check their published
   checksums, then extract the ZIP. It contains `Update-Sidecar.ps1` and `InstallationHealth.psm1`;
   both are also installed alongside the worker. Checksums establish file identity, not signing
   or trust. These packages remain unsigned previews.
4. Run the script from the extracted ZIP, using the server worker ID for this PC:

```powershell
.\Update-Sidecar.ps1 -Installer .\new.msi -Sha256 '<new MSI SHA-256>' `
    -PreviousInstaller .\previous.msi -PreviousSha256 '<previous MSI SHA-256>' `
    -ServerAddress https://optimisarr.example.com -WorkerId 7
```

The server's worker list API returns each worker's `id` and `name`. If it requires an admin token,
read it without putting it in the command line: `$token = Read-Host 'Server admin token' -AsSecureString`,
then add `-ServerToken $token`. Specify the existing folder with `-InstallDirectory` for a
non-default installation; both upgrade and recovery keep that folder.
The script checks the selected server/worker against the local monitor before updating.

You should see **Update verified** only after installed files match, the same replacement process
stays healthy for at least 90 seconds, and the server records at least three distinct fresh
check-ins. The local monitor must belong to that service process and report the installer build.
If the tray was open, it reopens for its original signed-in user and must remain running too.
An installed native window check also runs when the tray was previously closed.
Related Windows runtime, service and application-control failures block verification.
Missing diagnostics, stale evidence, changed drains or work starting during the check cannot pass.
After a verified result, remove the worker's drain on the server.

If the update fails, the script attempts recovery with the **whole previous MSI**, then applies
the same sustained checks to that worker. It does not substitute individual DLLs or change Windows
protections. Recovery removes the failed MSI first when necessary, allowing the previous version
to be installed. Pairing must remain byte-for-byte unchanged; a changed pairing, changed drain or
held jobs prevents automatic recovery. Media and scratch files are retained. A recovered update
still exits with an error and says **The previous installer was restored**; review the failure
before resuming it. Failed recovery stays explicitly unverified.

The script retains installers, logs, a result receipt and an encrypted pairing backup in an
Administrators/SYSTEM-only directory under `%ProgramData%\Optimisarr\Updates`. Treat this as
private recovery data, not a shareable diagnostics bundle. A timeout leaves installation
unverified and does not start a competing recovery while Windows Installer may still be running.
Exit code 3010 means Windows requires a restart and the update is unverified. Keep the worker
drained, restart and review it locally before resuming; the updater never reboots automatically.
The script does not resume the server worker automatically, including after recovery.

## Validation

The Windows build passes WiX package validation and supports administrative extraction. Native
pipe tests exercise read/pause/resume, ACL rules and invalid requests. The installed tray uses the
private runtime, renders isolated native fixture states, and checks real window anchoring across
page changes and disclosure expansion/collapse.

For actual installation/uninstallation, use a disposable elevated Windows VM with no existing
sidecar or pairing directory:

```powershell
./sidecars/windows/installer/test-evidence.ps1
./sidecars/windows/installer/test-process.ps1
./sidecars/windows/installer/test-health.ps1
./sidecars/windows/installer/build.ps1 -BuildUpgradeTest
./sidecars/windows/installer/test-install.ps1 -Installer (Get-ChildItem sidecars/windows/artifacts/*.msi).FullName -UpgradeInstaller ./sidecars/windows/artifacts/upgrade-test/OptimisarrSidecar-upgrade-test.msi
```

The test refuses an existing sidecar, installs silently into a custom folder, checks registration and private runtimes,
renders the installed UI and checks native popover anchoring. It verifies that an unpaired upgrade
stays stopped, pairs against a loopback fixture using real DPAPI storage, and runs two paired
upgrades with distinct MSI ProductCodes. Each must restart the service, preserve the pairing file
byte for byte, and sustain at least 30 seconds of health with three distinct authenticated
check-ins from the replacement process. It also exercises the public checked update route with
an open tray, then stops the disposable replacement worker after its first check-in. That update
must fail, restore the whole previous MSI, reopen the tray and pass sustained recovery checks.
The fixture
always drains the worker and never assigns media work. Finally, uninstall must retain pairing
and test data. Test credentials are cleaned up on this guarded disposable VM; MSI logs and
heartbeat evidence remain. CI stores them under its uploaded runner temporary directory;
failed runs also capture service registration and recent SCM/runtime events before uninstalling.
The workflow verifies that the installation log exists in the uploaded directory.
These diagnostics are limited to the guarded disposable test, not installed user machines.
A separate non-installing regression test simulates startup and diagnostic-provider failures,
proving that evidence is retained and collection never replaces the original error.
The process regression test uses an owned parent and child without touching an installer or service.
It proves the harness can finish an installer process while its worker stays alive, retains MSI
exit codes, and stops waiting at a deadline. Each MSI operation waits only for its own process;
the complete CI installation sequence has a further 20-minute limit. Non-installing health tests
cover delayed crashes, restart/build/process/worker mismatches, stale or missing check-ins,
tray failure, changed drains, unavailable/bounded Windows diagnostics, recovery ordering,
original-error preservation, failed recovery and restart-required or timed-out installers.
The upgrade fixture shares the staged payload but lives outside
published artifact globs. The
`Windows sidecar installer` GitHub workflow builds and runs this test on a fresh Windows
runner for relevant pull requests and manual dispatches. The live migration from a manually
registered service to MSI was tested on a paired Windows PC: installation succeeded, retained
the original pairing, and the installed service checked in with the server. A same-version preview upgrade was also exercised on that PC, preserving pairing and passing
the installed native anchoring check. Interactive UAC pairing remains a separate manual check.

## Distribution status

The MSI, tray apphost and project-owned managed assemblies are **unsigned development artifacts**.
Smart App Control or enterprise policy may reject an unsigned executable or assembly; do not
weaken those protections. Public distribution as a signed installer must follow the public [Code signing policy](../../../CODE_SIGNING_POLICY.md).
The SignPath Foundation application was declined because the project did not have enough
established reputation. Signing is not pending; the policy describes requirements for a future
signing arrangement and does not mean the current download is signed. An explicitly labelled
unsigned preview may be published only after its exact corresponding-source bundle/hosting for
the pinned GPL FFmpeg build is available. Upstream source/build links and runtime licences are included in the package;
those links alone do not establish that the corresponding sources have been supplied.
The installer does not change the server’s worker-placement or strict verification policy.

The [installer workflow](../../../.github/workflows/windows-installer.yml) uploads CI artifacts.
On a reviewed `vX.Y.Z` tag it also attaches the tested MSI and checksum to the matching draft release.
Publish the coordinated release only after container and both native package gates pass and matching
source archives are available. A tag must match `Directory.Build.props`, which supplies the default
MSI version. Use `build.ps1 -Version <version>` for an explicit local build and follow the repository
[release checklist](../../../docs/development/releasing.md).
