# Glossary

These terms appear throughout Optimisarr and the documentation.

| Term | Meaning |
|---|---|
| Activity watcher | A Plex, Jellyfin, or Emby connection that can pause new work while users are streaming. Running jobs are not interrupted. |
| Auto-optimise | A per-library setting that queues and starts eligible work inside that library's time window. |
| Auto-replace | A per-library setting that applies a verified output automatically. The original is still quarantined first. |
| Candidate | A discovered media file after Optimisarr has applied the library rules and decided whether it is eligible. |
| Dry-run mode | A global safety setting that allows scanning, probing, preview, transcoding, and verification, but refuses replacement and purge actions. |
| Eligible | A file that matches the current library rules and can be queued. |
| Exclusion | A path-keyed record telling Optimisarr not to optimise a file again until the exclusion is removed. |
| FFmpeg | The tool Optimisarr uses to transcode media and perform verification checks. |
| ffprobe | The tool Optimisarr uses to inspect media streams, codecs, duration, and container metadata. |
| Library | A configured root path plus media type, preset, rules, and automation settings. |
| Preview | A throwaway test encode for one file. Long video previews use a 60-second middle sample; audio and image previews run in full. |
| Quarantine | The `/trash` area where originals are kept after replacement so you can roll back until they are approved or purged. |
| Ready to replace | A queue state meaning the output has passed verification and can replace the original when dry-run is off. |
| Replacement | The act of moving the original into quarantine and moving the verified output into the library path. |
| Rollback | Restoring the quarantined original and removing the replacement. |
| Scan | A filesystem pass that discovers new, changed, or removed files in a library. |
| Verification gate | A check that must pass before an output can replace an original. Examples include decode health, duration tolerance, stream retention, size reduction, VMAF, loudness, true peak, SSIM, and metadata checks. |
| Work directory | The `/work` path where temporary transcode outputs are written before verification and replacement. |
