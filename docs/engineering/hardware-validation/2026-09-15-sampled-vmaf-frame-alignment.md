# Sampled-VMAF frame alignment on real fleet media — 2026-09-15

## Scope and safety

This investigation chased whole seasons failing perceptual-quality verification with harmonic means
in single figures while the encodes themselves were sound. It used two read-only source files
copied from the library, candidates encoded from them on a development Mac, and Optimisarr's own
supported retry endpoint against jobs that had already failed. No original was replaced, no
verification gate was weakened, and no database row, container, or deployment setting was edited by
hand.

The fixtures were two episodes of the same 480p VC-1 WEBRip season — chosen because one passed
verification on the fleet and one did not, which is the comparison the whole investigation rests on.

## Environment

| Item | Value |
| --- | --- |
| Control plane | Riker, Optimisarr 0.2.12.0 |
| Worker 3 | MacBook Air, macOS sidecar, `hevc_videotoolbox` |
| Worker 4 | PICARD, Windows sidecar, `hevc_nvenc` |
| Measurement | libvmaf `vmaf_v0.6.1`, three 40-second windows |

## What was observed

Verification reports showed a signature that is not a quality failure:

| job | mean | harmonic | lowest frame | frames below 5 |
| --- | --- | --- | --- | --- |
| 5967 | 68.78 | 21.17 | 0.00 | many |
| 5957 window 0 | 50.76 | 0.21 | 0.00 | 70 |
| 5957 window 1 | 52.62 | 0.05 | 0.00 | 252 |

A respectable mean beside a harmonic mean near zero means most frames scored well and a scattered
few scored nothing at all. That is two timelines out of step, not a weak encode.

## What was ruled out

Each of these was tested and discarded rather than argued about.

- **Frame dropping alone.** Fixed separately: FFmpeg's default frame-rate handling was dropping
  frames on sources ffprobe calls constant, and `-fps_mode passthrough` now prevents most of it. The
  failures continued afterwards, with source and candidate durations agreeing to 0.02%.
- **The encoder.** Reproduced on `hevc_videotoolbox` and `hevc_nvenc` independently.
- **The windowing.** The whole file measured end to end scored just as badly — mean 56.77, 387 of
  2151 sampled frames below 5 — so the fault is not in how windows are cut.
- **A `-fflags +genpts` asymmetry.** The encode reads the source with generated timestamps and the
  measurement does not, which on a VC-1 source carrying no PTS looked decisive. Adding it to the
  reference changed the result by nothing.

## What it was

The candidate's pictures sit a frame away from the source's, and the correction that exists for
exactly this — the `distortedShift` token in the measurement contract — was derived from the
containers' own headers:

```
fail-S13E20.mkv    container  0.122000  video  0.122000  lead +0.000000
cand-S13E20.mp4    container  0.000000  video  0.000000  lead +0.000000
pass-S13E21.mkv    container  0.269000  video  0.269000  lead +0.000000
```

The video stream's start equals the container's in every real container, so the shift computed as
zero for every file either sidecar had ever measured. **The mechanism had never once fired.**

Deriving it could not have worked in any case. The two fixtures are identical in every header field
— container start, stream start, first decoded frame — and need opposite corrections:

| pair | shift 0 | shift +1 frame | fleet result |
| --- | --- | --- | --- |
| S13E20 | harmonic 0.21 | **harmonic 92.32** | failed |
| S13E21 | **harmonic 89.88** | harmonic 0.31 | passed |

The difference is that frames are still occasionally lost in the encode — one in S13E20, six in
S13E21, measured with `ffprobe -count_frames` — so whether a given window lines up depends on how
many went missing before it. No arithmetic over metadata can know that.

## The change

All three machines now measure the alignment rather than deriving it: the candidate is tried a
frame either way against a two-second window and whichever matched best is what the real
measurement uses. About a second per job, the same probe and the same offsets on the server and
both sidecars, so a worker and the control plane cannot disagree about the same pair of files.

## A second fault found on the way

Twenty-four failures were traced to a different cause entirely. FFmpeg collapses consecutive
identical messages into `Last message repeated N times`, and the decode-health gate counted that
notice as a corrupt frame. The message being repeated was the muxer's `non monotonically increasing
dts` remark — which that gate already knows to ignore, and which NVENC and QSV emit routinely. The
notice is now read as what it is: an account of how many of the previous message there were.

## Result

Of eight jobs requeued after the fix, four passed. Three of the remaining four now report honest
quality figures — harmonic 73–80 with lowest frames of 49–67 and no zeros — which is the gate
correctly declining a candidate rather than a measurement failing to line up. One h264 job still
shows the old signature and is not yet explained.

## Still open

`-fps_mode passthrough` reduces frame loss but does not eliminate it. This work makes the
measurement honest about a candidate that lost frames; it does not stop them being lost.
