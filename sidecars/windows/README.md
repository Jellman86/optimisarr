# Optimisarr Windows sidecar

A Windows machine that contributes spare encoding capacity to an Optimisarr server, the way the
[macOS sidecar](../macos/README.md) does. Same protocol, same guarantees: the server decides what to
encode, this machine encodes it, and the candidate comes back for the server to verify. Nothing here
ever replaces, quarantines, moves or deletes a file.

## Why it is built this way

**A Windows service with a tray companion.** The macOS sidecar is a menu-bar app, so it only works while
someone is logged in. A desktop that spends the night at the login screen is exactly the machine
worth lending to a transcode queue, so the work belongs in a service that starts with the machine.

**A service cannot show a tray icon.** Session 0 has no desktop. So there are two processes: the
service does the work, and a small tray application, launched at logon, shows what it is doing and
talks to it locally. The tray is a window onto the service, never a requirement for it.

**FFmpeg is bundled.** Windows has no equivalent of "the FFmpeg everyone has", and builds vary
enormously in which encoders and which VMAF backends they carry. A pinned build is the only way a
capability probe means anything, and the only way the advertised capabilities can be relied on. The redistributable bundle uses CPU VMAF.

## Layout

```
src/Optimisarr.Sidecar.Core     Protocol, capability probing, job execution. No Windows types, so
                                it builds and is tested on any platform.
tests/                          xUnit over that core.
```

`Optimisarr.Sidecar.Service` hosts the worker; `Optimisarr.Sidecar.Tray` provides the compact
monitor above the Windows notification area. Preferences and diagnostics stay inside the panel.
The tray is optional: closing it leaves work running. See [installer notes](installer/README.md)
for the unsigned MSI preview, pairing, upgrade safeguards and release limitations.

## Capabilities

Every capability is **proved before it is advertised**, never read from `ffmpeg -encoders`. A build
carrying NVENC lists it on a machine with no NVIDIA card; a driver too old for the build fails when
the encoder is first opened, not when it is listed. The macOS sidecar shipped a bundled libx265 that
segfaulted on its first frame while being offered to the server as a capability, and a job scheduled
against a capability the machine cannot honour can only fail and come back.

So each of these runs for real at service start:

| Capability | How it is proved |
| --- | --- |
| Video encoders | Three frames of synthetic video, at a size that clears NVENC's minimum |
| Audio encoders | A fifth of a second of silence |
| Hardware decoders | Encode a clip, then decode it back with the accelerator engaged |
| VMAF | Score a clip against itself: CUDA first, then CPU |

Encoders looked for are libx264, libx265, libsvtav1, and the NVENC, Quick Sync and AMF families.
Audio is narrowed to what Optimisarr can actually ask for, including `libopus` and `libmp3lame`,
because claiming one the build lacks means a job handed over and refused with "Unknown encoder".

**NVIDIA VMAF** is the one capability that cannot be inferred at all. A build can carry
`libvmaf_cuda` while the machine has no usable CUDA device, and the server sends a GPU measurement
command on the strength of this answer alone, so it is scored for real.

## Setting up a machine to test on

See [Setting up a Windows test host](docs/test-host-setup.md): remote access, tooling, the scratch
folder, and optionally WSL with Docker for testing the server image against an NVIDIA GPU.

## Installing it on a machine

The build produces a managed assembly and **no executable of its own**, so everything runs through
`dotnet`:

```powershell
dotnet publish src\Optimisarr.Sidecar.Service\Optimisarr.Sidecar.Service.csproj -c Release -o C:\OptimisarrSidecar

# Pair first: the code is read from standard input, never from the command line.
"123456" | dotnet C:\OptimisarrSidecar\Optimisarr.Sidecar.Service.dll --pair https://optimisarr.example.com

# Then install and start the service.
dotnet C:\OptimisarrSidecar\Optimisarr.Sidecar.Service.dll --install
sc.exe start OptimisarrSidecar
```

**Why there is no .exe.** Smart App Control is on by default on Windows 11 and judges an executable
by its reputation. A freshly built, unsigned apphost has none, so the service is refused outright —
it will not start, and the system log says only `%%4551`. Every rebuild produces a new unknown file,
so it is not something that settles down with use. Signing would solve it and needs a certificate
this project does not have. `dotnet` is Microsoft-signed and trusted, and the managed assembly it
loads is not held to the same test.

The cost is that **the .NET runtime is a prerequisite** — the sidecar cannot carry its own. Install
the ASP.NET Core or .NET runtime for `net10.0` before installing the service. The installer looks
for `dotnet.exe` beside the running process, then under `DOTNET_ROOT`, then in
`%ProgramFiles%\dotnet`, and refuses to register a service it knows cannot start rather than
leaving a machine looking installed and doing nothing. It deliberately does not search the `PATH`:
that belongs to whoever ran the install, and the service runs as LocalSystem.

## Building and testing

```bash
cd sidecars/windows
dotnet test Optimisarr.Sidecar.slnx
```

The core targets `net10.0` and has no Windows-only dependencies, so this works on macOS and Linux
too. Anything that needs a real GPU is an explicit hardware acceptance run on a Windows machine.
