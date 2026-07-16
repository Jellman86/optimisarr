# Choose a personal quality setting

The **Personal quality check** is a full-page quality lab for finding the most space-efficient video
quality, audio bitrate, or image quality that still looks or sounds acceptable on your own equipment.
It is a personal calibration aid, not proof that two encodes are identical. Repeat it with several
representative sources before treating one result as a rule for a varied library.

## Before you begin

You need a saved Film, TV, Music, Photo, or Other library; a scanned and probed source; enough free
space under `/work`; and the display or listening equipment you normally use. Video and audio sources
must be long enough for three excerpts. Animated images and Dolby Vision video are not offered.

The source remains read-only. Optimisarr creates disposable jobs under `/work/calibration`; they
cannot replace, move, or delete media and do not appear in the normal Queue.

## Start a check

1. Open **Libraries**, configure a saved library, and select **Personal quality check**.
2. The dedicated quality-lab page opens. Choose a representative source containing the motion,
   texture, fine detail, ambience, or tonal range you care about.
3. Select **Prepare blind samples**.

Optimisarr prepares one unmodified reference and five candidates representing the relevant quality
ladder. It structurally verifies every candidate before the comparison becomes available. For HDR
video, preparation also requires Preserve HDR handling, a browser-reported HDR display path, and
your confirmation that the intended display is actually presenting HDR.

## Compare the anonymous A–F lineup

The six versions are shuffled and labelled only **A** through **F**. One is the original, but neither
the UI nor its label identifies it before reveal. Quality, encoder, bitrate, and estimated saving are
also hidden.

- Select A–F repeatedly while examining the same moment. Video and audio keep one shared relative
  position, and a switch is not shown until the destination stream has sought to the matching frame.
- Video provides three scene tabs, a shared 0–12-second timeline, and real browser fullscreen.
- Audio provides three 15-second excerpts. Optimisarr measures all six versions using EBU R128
  integrated loudness and attenuates each to the quietest one so volume cannot reveal a version.
- Still images use one viewport. Zoom or drag to inspect detail; zoom and pan remain unchanged while
  switching A–F.

Classify every letter as **Indistinguishable**, **Acceptable**, or **Visibly worse**. Desktop users
may drag a sample into a rating; the selection buttons provide the same operation for keyboard and
touch users. There are six classifications in total—no repeated trial loop.

## Reveal and apply the result

After all six samples are classified, select **Reveal samples and result**. Optimisarr then shows
which letter was the original, the setting behind every candidate, your classifications, estimated
savings, and the most compressed candidate you considered Indistinguishable or Acceptable. If every
candidate was Visibly worse, it recommends keeping the current setting.

**Use this quality for the library** changes only the saved video quality, audio bitrate, or image
quality. It does not scan, enqueue, replace, move, or delete media. Optimisarr refuses a stale result
if the relevant library codec, preset, or quality changed during the session.

## What each media type tests

| Media | Prepared comparison | Applied setting |
|---|---|---|
| Video | One frame-aligned original plus five quality levels across three 12-second scenes. | Video CRF/CQ quality. |
| Audio | One lossless reference plus five codec-appropriate bitrates across three level-matched 15-second excerpts. | Audio bitrate in kbps. |
| Still image | One lossless PNG reference plus five output-quality levels in a synchronized zoom/pan viewport. | Image quality. |

## If the check cannot continue

| What you see | What to do |
|---|---|
| **Personal quality check** is disabled | Save or discard the library's unsaved changes first. |
| No suitable source is ready | Scan and probe the library, then choose a long enough video/audio file or a non-animated image. |
| HDR viewing check blocks preparation | Use an HDR-capable browser/display and keep the library's HDR handling set to Preserve. |
| Preparation fails | Check `/work` space and permissions, then **Settings → Tools** for encoder availability. |
| A comparison stream cannot play | Return to the library and retry with a supported browser, codec, or source rather than guessing. |

Leaving the quality lab removes its session and scratch media. Abandoned sessions expire after two
hours, and restarting Optimisarr discards all remaining sessions. See the
[API reference](../api.md#personal-blind-quality-calibration) for the HTTP contract.
