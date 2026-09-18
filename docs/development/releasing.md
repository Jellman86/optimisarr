# Writing an Optimisarr release

Release notes should help someone answer three questions quickly:

1. What will I notice?
2. Is the update safer or easier for me?
3. Do I need to do anything before or after updating?

They are not a dump of commits. `CHANGELOG.md` remains the complete technical
record; the GitHub Release is the clear, human introduction to it.

## Start with evidence

Write from the versioned changelog and the checks completed for the exact tag.
Every claim must describe behaviour that is present in the released image. Do not
turn roadmap work, an open pull request, or an unverified host test into a release
promise.

Before writing, collect:

- the previous and new tags;
- the matching `CHANGELOG.md` section;
- the green tag build, test, container smoke test, and image publication;
- any migration, configuration, downtime, or compatibility action;
- the most important safety boundary or opt-in default affected by the release.

## Branch and image flow

1. Start `release/vX.Y.Z` from the exact reviewed `dev` commit. Change
   `Directory.Build.props` to `X.Y.Z`, rename the leading `## Unreleased`
   changelog section to `## X.Y.Z — YYYY-MM-DD`, and regenerate
   `docs/openapi.json`.
2. Run the Definition of Done, then open the release pull request into `main`.
   No everyday feature or fix pull request targets `main`.
3. Merge only with every required check green. Tag the exact reviewed merge
   commit as `vX.Y.Z`, then wait for the tag build, container smoke test, and
   versioned image publication before publishing the GitHub Release.
4. Immediately start a short-lived branch from current `dev` and synchronise
   only the released version metadata from `main`: the application version, the
   dated changelog heading, and the generated OpenAPI version. Add a fresh,
   empty `## Unreleased` section above the released section and open a pull
   request back into `dev`.
5. Merge the synchronization pull request only when its checks pass. Wait for
   the protected `dev` build to publish `ghcr.io/jellman86/optimisarr:dev`, then
   verify that image reports `X.Y.Z`.

This final synchronization is part of the release, not optional housekeeping.
Without it, the moving `dev` image can contain new code while continuing to
advertise the previous application version. CI checks the application, OpenAPI,
changelog, and newest Git tag together so the drift cannot pass the next
protected-branch build unnoticed.

## Container and sidecars together

A coordinated release uses the same reviewed `vX.Y.Z` commit for the container,
macOS app and Windows installer. Keep its GitHub Release as a **draft** until
the tag's CI, container publication and both packaging jobs have succeeded.
A green development build or a successful install of a locally built preview
does not establish that the downloadable release assets passed those checks.

Record the container digest, source commit, asset SHA-256 hashes, application
version and bundled media-tool revisions in the release evidence. Check the
version reported by `/api/health`, the Mac bundle's `CFBundleShortVersionString`,
the Windows MSI product version and the installed worker's check-in. Build
numbers distinguish successive Mac packages; protocol negotiation is separate
from the application version and is not proof that an endpoint was upgraded.

### macOS package

Use [the macOS release workflow](../../.github/workflows/sidecar-release.yml)
or [the release script](../../sidecars/macos/scripts/release-app.sh) from the
reviewed tag. A manual dispatch must select the release tag, not a moving branch.
The release version must agree with that tag and `Directory.Build.props`.
Legacy `sidecar-v*` tags identify earlier standalone Mac releases; they are not
container release tags.

The downloadable DMG and ZIP must contain the Developer ID signed, notarised
and stapled app. The DMG is notarised and stapled separately. Keep the
Gatekeeper and stapler validation output; an ad-hoc or merely signed local app
does not replace this distribution check. Verify that bundled FFmpeg and
ffprobe, Swift package resources and Precession icon assets survive packaging.

### Windows package

Use [the Windows installer workflow](../../.github/workflows/windows-installer.yml)
and [the installer guide](../../sidecars/windows/installer/README.md). Build the
MSI with the release version explicitly when invoking the script manually:

```powershell
./sidecars/windows/installer/build.ps1 -Version X.Y.Z
```

The installer test must pass on a clean Windows runner. Also retain evidence
for a paired upgrade: unchanged pairing, service restart and check-in, working
tray controls, and native placement after expanding details and navigating
Preferences. Validate the Start shortcut, executable and notification-area
icons from the installed package. Test the download's SHA-256 against its
published checksum, not a different local build.

An unsigned MSI remains an **unsigned Windows preview**, even when published
beside a stable container and notarised Mac app. State that beside the download;
do not describe it as signed or ask people to disable Smart App Control or
enterprise security policy. Packaging success alone does not establish signing.

### Bundled sources and notices

Before publishing either native binary package, retain and provide the exact
corresponding source and notices for its bundled GPL media tools, together with
the build scripts and revisions needed to reproduce them. Check the actual
payload; linking the FFmpeg project homepage is not evidence that the matching
source has been supplied.

- macOS builds FFmpeg with x264, x265, SVT-AV1, dav1d and libvmaf. Use the exact
  revisions in the packaged `BUILD-INFO.txt`, including for a cached vendor
  build. Resolving a moving upstream branch again can produce different source.
- Windows uses the exact archive and checksum pinned by
  [`fetch-ffmpeg.ps1`](../../sidecars/windows/scripts/fetch-ffmpeg.ps1). Preserve
  its corresponding dependency sources and build configuration, not just the
  Optimisarr source archive. Keep the private .NET and Windows Desktop runtime
  licence notices in the installer too.

Do not publish native assets while their matching source material is missing.
Source availability and package signing are separate checks.

Generate source packages from the committed release checkout with
[`package_sidecar_sources.py`](../../scripts/package_sidecar_sources.py):

```bash
python3 scripts/package_sidecar_sources.py --platform macos --output /new/mac-sources
python3 scripts/package_sidecar_sources.py --platform windows --output /new/windows-sources
```

The output directory must be new. The Mac command reads the bundled vendor
`BUILD-INFO.txt`; `--mac-sources /path/to/.build-ffmpeg` can reuse existing source
checkouts without modifying them. It archives the recorded commits, ignoring
working-tree changes. Copy the generated notices, `licenses` directory and
source manifest into the app before signing.

The Windows command downloads the checksum-pinned dependency cache from the
exact upstream build through authenticated `gh`, plus the recorded FFmpeg and
build-recipe commits. For an offline rerun, use
`--windows-cache /path/to/dependency-sources.zip`; the same checksum is required.
Upstream workflow artifacts expire, so retain that validated cache or the
published source parts. The manifest records every dependency archive and its
safe alias mapping; the published parts retain the complete upstream cache.

Publish every generated `.tar` part and its `.sha256` file beside the binary
downloads. Parts are kept below the release asset size limit and contain the
source manifest, notices and extraction/reproduction instructions. Regenerate
packages after changing the release commit: an older archive's Optimisarr
source revision must not be used to describe a newer binary build.

### Fleet verification and rollout

Use [the real-media acceptance harness](media-acceptance.md) with every required
physical worker named explicitly. Test ordinary verification and strict
sidecar-only verification separately. Keep the selected encoder/fixture matrix
and its omissions visible: a smoke run does not certify all drivers, HDR media
or full-length content.

Drain workers and wait for active leases to finish before installing. Preserve
pairing and settings, then confirm the installed version checks in and restore
its previous availability. Preserve an already selected strict sidecar verification policy during a mixed
upgrade. Incompatible workers cannot claim those assignments; temporary waiting
is preferable to silently changing the operator’s verification requirement.
For a worker-only library, missing compatible workers intentionally leaves work
waiting rather than falling back to the container. Server orchestration,
transfers, hashes and replacement still run on the server.

## Write for the update decision

Start from [the release-notes template](../../.github/RELEASE_NOTES_TEMPLATE.md).
Delete its comments and any section that has nothing useful to say.

Choose three to six changes a user will actually notice. Lead with the outcome:

> **More accurate storage checks.** Optimisarr now checks free space on the
> filesystem mounted at `/work`, so a small container disk no longer pauses a
> healthy queue.

Avoid leading with the implementation:

> Added mount-aware `DriveInfo` selection and orphan reconciliation.

The implementation belongs in the changelog. Release notes should still be
specific—“better performance” is not useful unless they say what became faster,
when, and whether there is a tradeoff.

## Voice and structure

- Use a calm, conversational tone. Write to `you`, not to “users”.
- Prefer short sentences and one idea per bullet.
- Use exact UI labels and familiar product terms.
- Put the benefit in bold at the start of each bullet.
- Describe safety honestly: what is protected, what is retained, and what remains
  opt-in.
- Put required action under **Before you update**, where it cannot be missed.
- Say **No special steps** when a normal image update is genuinely sufficient.
- Avoid hype such as “massive”, “game-changing”, and “best ever”.
- Avoid “we”; name Optimisarr or speak directly to `you`.

## Publish checklist

- [ ] The release title and tag match the application version.
- [ ] The exact tag's CI and container smoke test are green.
- [ ] Both native packaging jobs succeeded for the same reviewed commit, and
      downloadable assets, checksums and corresponding sources are present.
- [ ] Mac notarisation/stapling and Windows installation/upgrade evidence are
      retained; unsigned Windows downloads are visibly identified as previews.
- [ ] Every statement is supported by shipped code, tests, or verified operation.
- [ ] The opening summary makes sense without reading the changelog.
- [ ] Bullets describe user outcomes rather than internal components.
- [ ] **Before you update** states every required action—or says none is needed.
- [ ] Defaults, opt-in features, safety limits, and performance costs are honest.
- [ ] No token, private hostname, real library path, or other secret appears.
- [ ] The full changelog link points at the released tag.
- [ ] Empty template sections and all HTML comments are removed.
- [ ] The rendered GitHub preview has been read from top to bottom.
- [ ] Released metadata is synchronized back to `dev` through a green pull
      request and a fresh `## Unreleased` section exists.
- [ ] The rebuilt `:dev` image reports the released application version.
