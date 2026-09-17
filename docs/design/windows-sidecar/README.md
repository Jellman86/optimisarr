# Compact Monitor — Windows tray and Mac menu bar

The selected direction is **3 · Compact monitor**. The [native comparison](native.html) shows the implemented dark/light views. The three original interactive studies live
in `index.html`; their job data and proposed controls are simulated. Production native views use
real capabilities and readings instead.

Shared visual language: navy ground `#101A2C`, slate cards `#18253B`, raised slate `#203149`, teal
`#7BD8D1`, pale text `#EFF4FC`, secondary text `#B0BDD1`. Cards have a subtle diagonal texture and
stronger hover shadows. Native system typography keeps the small panel readable; data uses tabular
figures. Both clients follow system light/dark appearance. Windows also respects high contrast;
progress motion respects the system animation preference.

The panel leads with the current title and stage, then three live readings, expandable processing
details, a pause control, and nested Preferences/Diagnostics. No dashboard window is created.
Pairing may open a focused setup window (elevation is required for machine-wide Windows pairing).

## Platform differences that reflect real capabilities

- Mac retains its frame previews, film strip, one-to-four-job setting, work-location settings,
  login item and native menu-bar animation. Preferences now opens within the popover. CPU/GPU are
  real readings; macOS does not expose VideoToolbox media-engine utilisation. The third reading
  is jobs held. The full film strip is under Processing details.
- Windows gets a separate WPF tray companion using `NotifyIcon`. Work stays in the Windows
  service. The panel receives a credential-free snapshot over a local named pipe, and shows CPU,
  GPU and working-space capacity. Current assignments contain no artwork, so the media well
  honestly uses a film placeholder. The service currently accepts one job at a time; speculative
  Quiet/Balanced/Full preset controls are not shipped.
- No encode percentage or ETA is invented: the worker does not have an authoritative total
  duration. Encoding uses an indeterminate activity indicator and reports encoded time in details.
- Pause gates new claims, keeps heartbeat/lease renewal alive, and lets held jobs finish. A claim
  already in flight when paused is handed back. Pause resets when the worker/app restarts; the UI
  says so. On Windows, quitting the tray leaves the service running; quitting the Mac sidecar
  hands current jobs back using its existing shutdown path.

## Review and verification

The native renderers exercise idle, encoding, receiving/delivering, verification, disconnected,
and multi-job states as appropriate. They pose data and never pair or run jobs. Windows renders
are uploaded by the existing sidecar CI job. Mac snapshots use an actual AppKit hosting view so
native scroll views and menus are included.

Review corrections include offline/disabled state wording, clearing unavailable Windows readings,
keeping hover borders/shadows in both themes, removing stale server-verification wording, a pause
race during network claims, and framing/acknowledging local pipe replies before disconnecting.
Tests cover the race, pause preserving active work, safe server links, query redaction, status
presentation, local pipe round trips and Windows access rules.

The Windows pipe accepts only read/pause/resume bytes, bounds reply sizes and connection time,
permits local interactive users, and denies network logon tokens. It cannot accept arbitrary
commands or paths and never sends the worker credential. Pairing and starting the service remain
administrator actions.

The MSI is an unsigned development preview; see [installer notes](../../../sidecars/windows/installer/README.md).
The selected design has been installed on the paired Mac and Windows workers. The Windows
installation used the MSI migration route and preserved the existing pairing.

## Preview image licence

`bunny.jpg` is a frame from **Big Buck Bunny**, © 2008 Blender Foundation,
[bigbuckbunny.org](https://peach.blender.org/), used under
[Creative Commons Attribution 3.0](https://creativecommons.org/licenses/by/3.0/).
It was resized by the source video encoding and cropped by the preview UI. It is a design fixture,
not bundled artwork for users' media.
