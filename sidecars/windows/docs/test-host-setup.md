# Setting up a Windows test host

What a Windows machine needs before it can develop and prove the Optimisarr Windows sidecar, and
how to check each step actually worked. Written to be followed top to bottom on the machine itself.

Two halves, and they are independent. The **native** half builds and runs the sidecar service and
its tray application. The **container** half runs Optimisarr's own Docker image against an NVIDIA
GPU through WSL. Do the native half first; the container half only matters when working on GPU
VMAF in the server image.

> The native MSI installation, retained pairing, same-version upgrade and Compact Monitor native
> anchoring tests were exercised on a paired Windows test host on 2026-09-17. Historical **Verified 2026-09-14** notes
> below describe that host at that time; they do not guarantee the same Windows, driver or WSL
> behaviour on another machine. Check each step before proceeding. For ordinary use, start with
> the [MSI instructions](../installer/README.md); the SDK and WSL setup here are for development.

Where a step has a trap in it, the trap is written down rather than left for the next person to
rediscover.

---

## Native half

### 1. Remote access

SSH, because every command is then an ordinary shell command and the machine can be driven exactly
like any other host. PowerShell Remoting adds a second transport that only works well from a
PowerShell client and buys nothing SSH does not already provide; set it up if you want it, but
nothing here needs it.

Install the server first. **This does not produce a usable service in the same session:**

```powershell
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
```

The capability lands in state `InstallPending`, and the `sshd` service does not exist until the
machine restarts. Anything that tries to start or configure it before then fails with "Service sshd
was not found on computer". So **reboot here**, then configure the service:

```powershell
Set-Service -Name sshd -StartupType Automatic
Start-Service sshd
```

Both lines are needed, and they are needed *after* the reboot. The service is registered `Manual`
and stopped, so a machine that is only ever rebooted — the normal case for a host left running jobs
overnight — comes back with no SSH at all unless `StartupType` is changed.

Make PowerShell 7 the default shell, so commands arriving over SSH are not fighting `cmd.exe`
quoting. **Do not install it with `winget install --id Microsoft.PowerShell`.** That package now
ships only an `msixbundle`, which installs a per-user app-execution alias under
`%LOCALAPPDATA%\Microsoft\WindowsApps` and never creates `C:\Program Files\PowerShell\7\pwsh.exe`.
sshd runs as `LocalSystem` and resolves `DefaultShell` before any user context exists, so an alias
there silently fails and sshd falls back to `cmd.exe` — the exact outcome this step exists to
prevent.

Take the `.msi` for the current release from <https://github.com/PowerShell/PowerShell/releases>
and install it machine-wide:

```powershell
msiexec.exe /i PowerShell-<version>-win-x64.msi /qn /norestart
New-ItemProperty -Path "HKLM:\SOFTWARE\OpenSSH" -Name DefaultShell `
  -Value "C:\Program Files\PowerShell\7\pwsh.exe" -PropertyType String -Force
```

Authorise a key rather than a password. For an account in the local Administrators group Windows
reads a **shared** file, not the user's own `authorized_keys`:

```powershell
$key = "ssh-ed25519 AAAA... comment"
Add-Content -Path C:\ProgramData\ssh\administrators_authorized_keys -Value $key
icacls C:\ProgramData\ssh\administrators_authorized_keys /inheritance:r `
  /grant "Administrators:F" /grant "SYSTEM:F"
```

Two things about that file. The `icacls` line matters: sshd refuses the file if anyone else can
write it, and fails silently back to password authentication. And it must be ASCII, or UTF-8
**without** a byte-order mark — `Add-Content` is fine, but a redirect from some editors writes a
BOM, which sshd will not parse and will not warn about either.

**Check it:** from the other machine, `ssh <host> "whoami"` returns an admin account without
prompting for a password.

### 2. Why the account needs Administrators

Installing, starting and stopping a Windows service requires it, as does reading the service's
entries from the Event Log. Nothing here needs a Microsoft account, and nothing needs RDP.

RDP is worth knowing about for one thing only: the tray application cannot be seen over SSH. On a
Pro edition it is usually already enabled and listening, so it costs nothing to use when a layout
needs eyes on it. Two caveats. If the account is a Microsoft account, sign in as
`MicrosoftAccount\<email>` with the account password, because a Windows Hello PIN does not work
remotely. And a client SKU allows one active session, so connecting over RDP disconnects whoever is
at the physical console.

### 3. Tooling

Install the .NET 10 SDK and Git **on the machine**, and build there rather than copying binaries in.
Cross-compiling from macOS works, but every step between an edit and the thing that runs is a step
that can mislead: building where the code runs removes a whole class of "is that really the binary
I just changed" confusion, and the hardware-dependent tests have to run there anyway.

```powershell
winget install --id Microsoft.DotNet.SDK.10 --silent
winget install --id Git.Git --silent
```

`global.json` pins the SDK feature band with `rollForward: latestFeature`, so any 10.0.x at or above
the pinned version satisfies it.

**Check it:** `dotnet --version` reports 10.x, `git --version` answers, and
`dotnet test sidecars\windows\tests\Optimisarr.Sidecar.Core.Tests` passes. A green suite proves the toolchain
far better than a version string does.

**Verified 2026-09-14** on PICARD: .NET SDK 10.0.401, pwsh 7.6.6, suite green.

**Do not clone into OneDrive.** PICARD's checkout is under
`C:\Users\<user>\OneDrive\Documents\GitHub`, and sync can lock files mid-build and leave conflict
copies, the same way iCloud does on a Mac. When a build fails in a way that makes no sense, suspect
that before the code. Somewhere outside a synced folder is one less thing to rule out.

### 4. Network

The machine must reach the Optimisarr server to pair, fetch sources and deliver candidates.

**Check it:** `curl <scheme>://<server>:8787/api/health` returns `"status":"healthy"`. If the server
sits behind a reverse proxy, use the proxied URL and the scheme it actually serves — a proxy that
redirects plain HTTP answers with a `301` rather than the health payload, which reads like a failure
and is not one. If the machine is on a different VLAN from the server, settle that now rather than
debugging it later as a pairing failure.

### 5. A scratch folder

A job downloads its source and writes its candidate before sending it back, together about one and
a half times the size of the original. Somewhere with room, ideally not the system drive — though a
single-volume machine is fine, and a second drive is not worth adding for this alone:

```powershell
New-Item -ItemType Directory -Force -Path <drive>:\OptimisarrWork
```

### 6. Graphics driver

For the NVIDIA hardware tests below, install the host’s NVIDIA driver from Windows Update or
NVIDIA. The native sidecar can also use other encoders that pass its capability probes. The WSL
GPU path uses the Windows host driver; **do not** install a second GPU driver inside WSL.

**Check it:** `nvidia-smi` lists the card.

---

## Container half

Only needed for GPU VMAF work in the Optimisarr server image.

### 7. WSL with its own SSH

Reach the distro directly rather than through `wsl.exe` from a Windows session. Nesting two shells
makes quoting miserable and error-prone, and container work is quoting-heavy.

```powershell
wsl --install -d Ubuntu
```

**Confirm `VirtualMachinePlatform` is enabled before going further.** WSL runs without it by falling
back through the Hyper-V path, but it cannot create its NAT virtual switch, so every invocation
prints `Failed to configure network (networkingMode Nat), falling back to networkingMode
VirtioProxy` and the distro ends up sharing the host's address by accident rather than by design:

```powershell
Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform
Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -All
```

Then set mirrored networking in `%USERPROFILE%\.wslconfig`:

```ini
[wsl2]
networkingMode=mirrored
firewall=true

[experimental]
hostAddressLoopback=true
```

Mirrored is worth choosing deliberately rather than inheriting. Under the default NAT mode the
distro gets its own address that changes on every restart, so reaching its sshd from another machine
needs a `netsh portproxy` re-pointed at that address on every boot. Mirrored gives the distro the
host's addresses instead — including any VPN or overlay interface such as Tailscale, which NAT mode
cannot offer without further forwarding — so there is no proxy to maintain and nothing to re-run.

**If a portproxy already exists on this port, delete it.** Under mirrored networking the Windows
listener occupies the same namespace the distro binds in, and sshd then fails to start with
`Bind to port 2222 on 0.0.0.0 failed: Address already in use`:

```powershell
netsh interface portproxy delete v4tov4 listenport=2222 listenaddress=0.0.0.0
```

Administer the distro as root rather than reaching for `sudo`. A fresh distro prompts for a password
no script can supply, and `wsl -u root` needs none.

Pipe long or quote-heavy commands in rather than passing them as arguments. `wsl.exe` re-quotes its
argument list, which turns `'single quotes'` into `"double quotes"` and lets the wrong shell expand
`$(...)` — the failure looks like a bash syntax error coming from nowhere:

```powershell
@'
apt-get update && apt-get install -y openssh-server
sed -i 's/^#\?Port .*/Port 2222/' /etc/ssh/sshd_config
'@ | wsl -d Ubuntu -u root -- bash -s
```

Editing `Port` is not enough on its own. Ubuntu 24.04 socket-activates ssh, so the listening port
comes from `ssh.socket` and the `sshd_config` value is ignored — the distro stays on port 22, with
no error anywhere to say so. Disable the socket and enable the service:

```powershell
@'
systemctl disable --now ssh.socket
systemctl enable --now ssh.service
'@ | wsl -d Ubuntu -u root -- bash -s
```

Authorise the same key for the distro user, then allow the port in from outside:

```powershell
New-NetFirewallRule -DisplayName "WSL SSH" -Direction Inbound -LocalPort 2222 `
  -Protocol TCP -Action Allow
```

**Check it:** `ssh -p 2222 <user>@<host> "uname -a"` from the other machine reports Linux. Note that
`Get-NetTCPConnection -LocalPort 2222` on the Windows side shows nothing under mirrored networking,
because the distro's listeners do not appear in the host's table. Connect to the port to test it;
do not read the table and conclude the service is down.

### 8. Docker Engine, not Docker Desktop

Install Docker Engine **inside the distro**. Docker Desktop needs a logged-in user session, which is
the exact constraint the sidecar service exists to avoid, and it would make unattended testing
depend on somebody being signed in.

```powershell
@'
curl -fsSL https://get.docker.com | sh
usermod -aG docker <user>
systemctl enable --now docker
'@ | wsl -d Ubuntu -u root -- bash -s
```

Docker Desktop already being installed is not a problem and does not need removing. Its CLI shim is
injected into the distro's `PATH`, but Engine installs to `/usr/bin/docker`, which takes precedence.
Confirm with `which docker` before assuming which daemon a command reached.

### 9. GPU inside containers

```powershell
@'
curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey \
  | gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
curl -fsSL https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
  | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#' \
  > /etc/apt/sources.list.d/nvidia-container-toolkit.list
apt-get update && apt-get install -y nvidia-container-toolkit
nvidia-ctk runtime configure --runtime=docker
systemctl restart docker
'@ | wsl -d Ubuntu -u root -- bash -s
```

**Check it:** this prints the card from inside a container, which is the whole point:

```powershell
wsl -d Ubuntu -u root -- docker run --rm --gpus all nvidia/cuda:12.6.2-base-ubuntu24.04 nvidia-smi
```

**Verified 2026-09-14** on PICARD, driven from another machine over SSH:
`NVIDIA GeForce RTX 4070, 12282 MiB, 616.92`.

**That command fails when run over SSH rather than at the console**, with a message that says
nothing about the real cause:

```
docker: error getting credentials - err: exit status 1,
out: `A specified logon session does not exist. It may already have been terminated.`
```

Docker Desktop's credential helper is on the distro's `PATH` and wants an interactive Windows logon
session, and a non-interactive SSH session has none. The image is public and needs no credentials at
all, so point Docker at a config that has no credential store:

```powershell
wsl -d Ubuntu -- mkdir -p /tmp/dockercfg
wsl -d Ubuntu -- bash -c "echo {} > /tmp/dockercfg/config.json"
wsl -d Ubuntu -- env DOCKER_CONFIG=/tmp/dockercfg docker run --rm --gpus all `
  nvidia/cuda:12.6.2-base-ubuntu24.04 nvidia-smi
```

This is the single most likely thing to make a correctly built host look broken.

### 10. Reaching the machine when nobody is logged in

The two SSH endpoints do not behave the same way, and the difference decides how the host can be
used.

Windows sshd runs as `LocalSystem` and starts at boot, so **port 22 answers with nobody logged in at
all**. That is the property the sidecar service itself depends on, and it is why the native half
needs nothing further here.

WSL is different. Distro instances are per-user and start on demand, so with nobody logged in the
distro is not running and **port 2222 is not listening**. This needs no scheduled task and no stored
credentials to fix: SSH to Windows, run any `wsl` command, and the distro boots, systemd starts, and
sshd comes up behind it.

```powershell
wsl -d Ubuntu -u root -- systemctl is-active ssh
```

Verified from a cold stop — after `wsl --shutdown` port 2222 refuses connections, and after one
`wsl` command it answers on loopback, LAN and the overlay address.

**A different symptom means a stale portproxy.** On PICARD after a reboot on 2026-09-14, port 2222
**accepted** the TCP connection and then reset it during key exchange, rather than refusing it:

```
kex_exchange_identification: read: Connection reset by peer
```

That is a Windows `netsh portproxy` listener still in place with nothing behind it — the one step 7
says to delete under mirrored networking. Refused means the distro is simply not running; accepted
then reset means something is listening on Windows that should not be. Check with
`netsh interface portproxy show v4tov4`. So the container half is always
at most one command away, and never needs anyone signed in at the console.

---

## How the sidecar is built to be tested

Two decisions in the sidecar itself matter more to iteration speed than any of the above.

**It runs as a console application as well as a service.** `UseWindowsService()` runs as a service
when the service manager starts it and as an ordinary console program otherwise — same binary, same
code path. So ordinary work is: build, run it directly, watch it pair and take a job with output on
the terminal. Installing it as a service is reserved for the handful of things that are genuinely
service-specific: starting with nobody logged in, surviving a logoff, running as LocalSystem.
Without that, every change costs a stop, uninstall, copy, install and start, and a service that
fails at startup tells you almost nothing.

**It logs to the Event Log as a service, and to the console when run directly.** Read current
service activity from Windows Logs → Application, source `OptimisarrSidecar`. There is no rolling
file logger in the current service; do not wait for a log file that it does not create.

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'OptimisarrSidecar' } -MaxEvents 30
```

**The tray renders its states to images.** The tray cannot be seen over SSH, so it takes a flag that
writes fixture states to disk (`--render-monitor <directory>`), the way the macOS sidecar’s
`--render-menu` does. The fixtures do not connect to the live worker. `--verify-popover` additionally
drives the real WPF window through pages and disclosure changes and checks its working-area anchor.
`--render-tray-motion <directory>` exports six native-size frames in each system theme through the
same atlas-to-icon path the notification area uses, without starting or pairing the worker.
The Compact Monitor's media imagery fixtures include `preview-fallback` and `two-jobs`; all
artwork is synthetic. For a real decoder check on this host, set `OPTIMISARR_PREVIEW_FFMPEG` to
the installed `ffmpeg.exe` path and run the `Native_ffmpeg_extracts_a_small_frame_and_falls_back_for_audio_only`
test. It creates a temporary generated video and audio-only file and leaves the live service alone.

---

## What each half unlocks

| Half | Makes it possible to prove |
| --- | --- |
| Native | Proved hardware encoding/decode, CPU VMAF in the bundled redistributable toolchain, service lifecycle, MSI installation, tray behaviour and real jobs against the server. CUDA VMAF requires a separately compatible toolchain and a successful probe. |
| Container | Real media acceptance and, when the image carries a compatible CUDA VMAF build, GPU measurement on a passed-through NVIDIA device. See the [acceptance harness](../../../docs/development/media-acceptance.md). |
