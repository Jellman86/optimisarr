# Optimisarr Windows sidecar

A Windows machine that contributes spare encoding capacity to an Optimisarr server, the way the
[macOS sidecar](../macos/README.md) does. Same protocol, same guarantees: the server decides what to
encode, this machine encodes it, and the candidate comes back for the server to verify. Nothing here
ever replaces, quarantines, moves or deletes a file.

## Why it is built this way

**A Windows service, not an app.** The macOS sidecar is a menu-bar app, so it only works while
someone is logged in. A desktop that spends the night at the login screen is exactly the machine
worth lending to a transcode queue, so the work belongs in a service that starts with the machine.

**A service cannot show a tray icon.** Session 0 has no desktop. So there are two processes: the
service does the work, and a small tray application, launched at logon, shows what it is doing and
talks to it locally. The tray is a window onto the service, never a requirement for it.

**FFmpeg is bundled.** Windows has no equivalent of "the FFmpeg everyone has", and builds vary
enormously in which encoders and which VMAF backends they carry. A pinned build is the only way a
capability probe means anything, and the only way NVIDIA hardware VMAF can be relied on.

## Layout

```
src/Optimisarr.Sidecar.Core     Protocol, capability probing, job execution. No Windows types, so
                                it builds and is tested on any platform.
tests/                          xUnit over that core.
```

The service, tray and installer follow. Keeping the core free of Windows types is deliberate: it is
the part worth testing exhaustively, and it should not need a Windows machine to do so.

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

## Building and testing

```bash
cd sidecars/windows
dotnet test Optimisarr.Sidecar.slnx
```

The core targets `net10.0` and has no Windows-only dependencies, so this works on macOS and Linux
too. Anything that needs a real GPU is an explicit hardware acceptance run on a Windows machine.
