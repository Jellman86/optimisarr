# Screenshot gallery

See the main application and its Mac, Windows and Linux sidecars before setting them up.
These 0.2.22 captures use fabricated dummy media created for documentation, original artwork,
invented machine names and example server addresses. No copyrighted material or private
library data is used. Readings illustrate the interface; they are not performance benchmarks.

## Main application

The Queue shows the current encode, its progress and the jobs waiting for review or replacement.
The server keeps authority over replacement and quarantine; workers return candidates and evidence.

![Queue with a fabricated active video encode, workload lanes and job history](../images/optimisarr-queue-dark.png)

| Explore | What it shows |
|---|---|
| [Dashboard](../images/optimisarr-dashboard-dark.png) | Lifetime savings, recent results and service health. |
| [Libraries](../images/optimisarr-libraries-dark.png) | Media paths, presets and workflow controls. |
| [Inventory](../images/optimisarr-inventory-dark.png) | Discovered files and their media facts. |
| [Quarantine review](../images/optimisarr-quarantine-review-dark.png) | Original/candidate comparison and verification before a decision. |
| [Remote worker settings](../images/optimisarr-settings-workers-dark.png) | Pairing, capabilities and server-side worker controls. |
| [Personal quality check](../images/optimisarr-personal-quality-video-dark.png) | Anonymous candidates compared with the marked reference. |

See [the user workflow](workflow.md) for the steps behind these screens.

### Playback pauses and privacy

Queue and Schedule explain which playbacks are holding new work. The status strip links to Queue
for the full details, including on a phone. Names and devices can be hidden per media server.

![Schedule paused for fabricated film, episode and music playback with viewer and device details](../images/optimisarr-schedule-playback-dark.png)

| Phone Queue | Viewer names hidden |
|---|---|
| <img src="../images/optimisarr-queue-playback-mobile-dark.png" width="260" alt="Phone Queue showing three fabricated playback holds above the waiting jobs"> | <img src="../images/optimisarr-settings-viewers-hidden-dark.png" width="650" alt="Saved media-server connection labelled Viewers hidden, with titles still shown in the playback pause"> |

See [media-server settings](../integrations/media-servers.md#what-a-playback-pause-shows) for the
privacy control and a title-only Queue example.

## Sidecars

Mac and Windows use native desktop panels. Linux uses a browser dashboard hosted by the worker.
All three show work and available resource readings; missing telemetry stays unavailable.

### Mac menu bar

| Working, dark | Working, light |
|---|---|
| <img src="../images/optimisarr-sidecar-macos-encoding.png" width="390" alt="Dark Mac Compact Monitor encoding the fabricated Prism Field clip with stage and resource readings"> | <img src="../images/optimisarr-sidecar-macos-light-encoding.png" width="390" alt="Light Mac Compact Monitor showing the same fabricated encode"> |

| Processing details | Preferences |
|---|---|
| <img src="../images/optimisarr-sidecar-macos-details.png" width="390" alt="Mac Processing details showing receive, encode, verify and return stages for a fabricated job"> | <img src="../images/optimisarr-sidecar-macos-light-preferences.png" width="390" alt="Mac Preferences with work location and login controls"> |

| State | Dark | Light |
|---|---|---|
| Ready for work | [Idle](../images/optimisarr-sidecar-macos-idle.png) | [Idle](../images/optimisarr-sidecar-macos-light-idle.png) |
| Before pairing | [Pairing](../images/optimisarr-sidecar-macos-pairing.png) | [Pairing](../images/optimisarr-sidecar-macos-light-pairing.png) |
| Two jobs | [Activity](../images/optimisarr-sidecar-macos-two-jobs.png) | [Activity](../images/optimisarr-sidecar-macos-light-two-jobs.png) |
| Audio with generated spectrum | [Audio](../images/optimisarr-sidecar-macos-audio.png) | [Audio](../images/optimisarr-sidecar-macos-audio-light.png) |
| Cancelable shutdown countdown | [Countdown](../images/optimisarr-sidecar-macos-shutdown.png) | [Countdown](../images/optimisarr-sidecar-macos-light-shutdown.png) |
| Processing details | Shown above | [Details](../images/optimisarr-sidecar-macos-light-details.png) |
| Preferences | [Preferences](../images/optimisarr-sidecar-macos-preferences.png) | Shown above |

These are actual native views rendered offscreen by the 0.2.22 app. The renderer uses a flat
snapshot background and bordered controls; macOS 26 supplies glass in the live popover.
See the [Mac installation guide](../../sidecars/macos/README.md).

### Windows tray

| Working, dark | Working, light |
|---|---|
| <img src="../images/optimisarr-sidecar-windows-encoding.png" width="390" alt="Dark Windows Compact Monitor encoding the fabricated Prism Field clip with CPU and GPU load"> | <img src="../images/optimisarr-sidecar-windows-light-encoding.png" width="390" alt="Light Windows Compact Monitor showing the same fabricated encode"> |

| Two jobs in Processing details | Preferences |
|---|---|
| <img src="../images/optimisarr-sidecar-windows-two-jobs.png" width="390" alt="Windows Processing details with separate fabricated Prism Field and Orbit Study job previews"> | <img src="../images/optimisarr-sidecar-windows-light-preferences.png" width="390" alt="Windows Preferences with pairing, worker start and tray sign-in controls"> |

| State | Dark | Light |
|---|---|---|
| Ready for work | [Idle](../images/optimisarr-sidecar-windows-idle.png) | — |
| One job's Processing details | [Details](../images/optimisarr-sidecar-windows-details.png) | [Details](../images/optimisarr-sidecar-windows-light-details.png) |
| Two jobs | Shown above | [Activity](../images/optimisarr-sidecar-windows-light-two-jobs.png) |
| Audio preview unavailable | [Labelled fallback](../images/optimisarr-sidecar-windows-audio.png) | [Labelled fallback](../images/optimisarr-sidecar-windows-audio-light.png) |
| Video preview unavailable | [Labelled fallback](../images/optimisarr-sidecar-windows-preview-fallback.png) | [Labelled fallback](../images/optimisarr-sidecar-windows-light-preview-fallback.png) |
| Cancelable shutdown countdown | [Countdown](../images/optimisarr-sidecar-windows-shutdown.png) | [Countdown](../images/optimisarr-sidecar-windows-light-shutdown.png) |
| Preferences | [Preferences](../images/optimisarr-sidecar-windows-preferences.png) | Shown above |

These are native WPF renders from the current Windows CI build. Its audio fixture does not
supply a spectrum, so that capture demonstrates the labelled fallback. The background service
continues working when the tray closes. See the [Windows guide](../../sidecars/windows/README.md).

### Linux container

The worker page shows the source facts, progress, local preview, proved capabilities and recent
returns. Pairing happens here; scheduling, pause and drain controls stay on the main server.

![Dark Linux worker dashboard encoding a fabricated clip, with source preview, CPU load and scratch space](../images/optimisarr-sidecar-linux-encoding-dark.png)

| Light dashboard | Phone dashboard |
|---|---|
| <img src="../images/optimisarr-sidecar-linux-encoding-light.png" width="650" alt="Light Linux dashboard showing the fabricated Lumen Coast encode and worker capabilities"> | <img src="../images/optimisarr-sidecar-linux-encoding-phone.png" width="260" alt="Linux worker dashboard stacked for a phone, showing encode progress, source preview and capabilities"> |

| State | Dark | Light | Phone |
|---|---|---|---|
| Pairing | [Pairing form](../images/optimisarr-sidecar-linux-pairing-dark.png) | [Pairing form](../images/optimisarr-sidecar-linux-pairing-light.png) | [Pairing form](../images/optimisarr-sidecar-linux-pairing-phone.png) |
| Ready for work | [Idle](../images/optimisarr-sidecar-linux-idle-dark.png) | [Idle](../images/optimisarr-sidecar-linux-idle-light.png) | [Idle](../images/optimisarr-sidecar-linux-idle-phone.png) |
| Audio with generated spectrum | [Audio](../images/optimisarr-sidecar-linux-audio-dark.png) | [Audio](../images/optimisarr-sidecar-linux-audio-light.png) | [Audio](../images/optimisarr-sidecar-linux-audio-phone.png) |

See [Linux container setup](../setup/linux-sidecar.md) and [worker placement and verification](../setup/remote-workers.md).

## Capture details

[Capture instructions and manifests](../images/README.md) record source versions, fixture data,
native-renderer limitations and repeatable commands. Dated review evidence is kept separately
and retains its original screenshots.
