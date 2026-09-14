# Setting up a Windows test host

What a Windows machine needs before it can develop and prove the Optimisarr Windows sidecar, and
how to check each step actually worked. Written to be followed top to bottom on the machine itself.

Two halves, and they are independent. The **native** half builds and runs the sidecar service and
its tray application. The **container** half runs Optimisarr's own Docker image against an NVIDIA
GPU through WSL. Do the native half first; the container half only matters when working on GPU
VMAF in the server image.

---

## Native half

### 1. Remote access

SSH, because every command is then an ordinary shell command and the machine can be driven exactly
like any other host. PowerShell Remoting adds a second transport that only works well from a
PowerShell client and buys nothing SSH does not already provide; set it up if you want it, but
nothing here needs it.

```powershell
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
Start-Service sshd
Set-Service -Name sshd -StartupType Automatic
```

Make PowerShell the default shell, so commands arriving over SSH are not fighting `cmd.exe` quoting:

```powershell
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

That last line matters: sshd refuses the file if anyone else can write it, and fails silently back
to password authentication.

**Check it:** from the other machine, `ssh <host> "whoami"` returns an admin account without
prompting for a password.

### 2. Why the account needs Administrators

Installing, starting and stopping a Windows service requires it, as does reading the service's
entries from the Event Log. Nothing here needs a Microsoft account, and nothing needs RDP.

### 3. Tooling

Install the .NET 10 SDK and Git **on the machine**, and build there rather than copying binaries in.
Cross-compiling from macOS works, but every step between an edit and the thing that runs is a step
that can mislead: building where the code runs removes a whole class of "is that really the binary
I just changed" confusion, and the hardware-dependent tests have to run there anyway.

```powershell
winget install --id Microsoft.DotNet.SDK.10 --silent
winget install --id Git.Git --silent
```

**Check it:** `dotnet --version` reports 10.x and `git --version` answers.

### 4. Network

The machine must reach the Optimisarr server to pair, fetch sources and deliver candidates.

**Check it:** `curl http://<server>:8787/api/health` returns `"status":"healthy"`. If the machine is
on a different VLAN from the server, settle that now rather than debugging it later as a pairing
failure.

### 5. A scratch folder

A job downloads its source and writes its candidate before sending it back, together about one and
a half times the size of the original. Somewhere with room, ideally not the system drive:

```powershell
New-Item -ItemType Directory -Force -Path D:\OptimisarrWork
```

### 6. Graphics driver

Install the current NVIDIA driver from Windows Update or NVIDIA. This is the only driver install
needed, and it also covers the WSL half below — **do not** install a driver inside WSL.

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

Then inside the distro, put sshd on a spare port and authorise the same key:

```bash
sudo apt-get update && sudo apt-get install -y openssh-server
sudo sed -i 's/^#\?Port .*/Port 2222/' /etc/ssh/sshd_config
sudo service ssh restart
```

Forward the port from Windows so the distro is reachable from another machine:

```powershell
netsh interface portproxy add v4tov4 listenport=2222 listenaddress=0.0.0.0 `
  connectport=2222 connectaddress=(wsl hostname -I).Trim()
New-NetFirewallRule -DisplayName "WSL SSH" -Direction Inbound -LocalPort 2222 `
  -Protocol TCP -Action Allow
```

WSL's address changes when it restarts, so that proxy needs re-running after a reboot, or scripting
at logon.

**Check it:** `ssh -p 2222 <host> "uname -a"` from the other machine reports Linux.

### 8. Docker Engine, not Docker Desktop

Install Docker Engine **inside the distro**. Docker Desktop needs a logged-in user session, which is
the exact constraint the sidecar service exists to avoid, and it would make unattended testing
depend on somebody being signed in.

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER
```

### 9. GPU inside containers

```bash
curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey \
  | sudo gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
curl -fsSL https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
  | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#' \
  | sudo tee /etc/apt/sources.list.d/nvidia-container-toolkit.list
sudo apt-get update && sudo apt-get install -y nvidia-container-toolkit
sudo nvidia-ctk runtime configure --runtime=docker
sudo service docker restart
```

**Check it:** this prints the card from inside a container, which is the whole point:

```bash
docker run --rm --gpus all nvidia/cuda:12.6.0-base-ubuntu24.04 nvidia-smi
```

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

**It logs to a file as well as the Event Log.** The Event Log is the idiomatic place and awkward to
read remotely; a rolling file is `Get-Content -Tail`. The macOS sidecar shipped with no logging at
all and it cost real time diagnosing a hang.

**The tray renders its states to images.** The tray cannot be seen over SSH, so it takes a flag that
writes a picture of every state to disk, the way the macOS sidecar's `--render-menu` does. Reviewing
a layout should not require someone to take a screenshot.

---

## What each half unlocks

| Half | Makes it possible to prove |
| --- | --- |
| Native | NVENC encoding, CUDA decode and NVIDIA VMAF on real hardware; the service starting with nobody logged in; the installer; real jobs against the server |
| Container | Whether a CUDA-enabled VMAF build in the server image genuinely scores on the GPU, which is currently a decision made on reasoning rather than measurement |
