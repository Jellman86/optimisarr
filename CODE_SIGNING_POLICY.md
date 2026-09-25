# Optimisarr code signing policy

This policy defines which Optimisarr release artifacts may be signed, how their
source and build are established, who may approve signing, and what information
the software sends over a network.

Optimisarr intends to use the SignPath Foundation open-source programme for its
Windows releases. Approval and production signing are currently pending. The
required attribution for artifacts signed through that programme is:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

See [SignPath.io](https://signpath.io/) and the
[SignPath Foundation](https://signpath.org/) for the service and certificate
programme.

Until SignPath has accepted the project and a release passes every control in
this policy, Windows downloads remain unsigned previews and must be labelled as
such. This policy is not evidence that a particular file has been signed.

## Signing scope

Only artifacts built from source owned and maintained in the public
[`Jellman86/optimisarr`](https://github.com/Jellman86/optimisarr) repository may
use the Optimisarr signing policy:

- the final `OptimisarrSidecar-X.Y.Z-win-x64.msi` installer; and
- project-owned portable executable files inside that installer, specifically
  `Optimisarr.Sidecar.Tray.exe` and managed assemblies whose names begin with
  `Optimisarr`.

The project certificate must not be used to sign upstream or third-party
binaries. In particular, bundled FFmpeg/ffprobe, the private Microsoft .NET and
Windows Desktop runtimes, framework assemblies, and build tools retain their
upstream signatures or remain unsigned. The SignPath artifact configuration
must select project-owned files explicitly, preserve or verify existing
third-party signatures, and reject an unexpected payload layout.

The macOS application uses Apple's Developer ID and notarisation process. The
container image and source archives use their own release provenance and digest
checks. Neither is signed through this Windows policy.

## Trusted build and release path

Production signing is restricted to the public Windows installer workflow in
[`.github/workflows/windows-installer.yml`](.github/workflows/windows-installer.yml).
The request must originate from a GitHub-hosted runner for an exact reviewed
`vX.Y.Z` tag whose commit is the approved release state on `main`. Pull requests,
forks, local builds, moving branches, workflow rehearsals, and manually uploaded
binaries cannot request a production signature.

The release path must:

1. validate that the tag, application version, changelog and source commit
   agree;
2. build and test the unsigned installer from that checkout on a GitHub-hosted
   Windows runner;
3. upload the unsigned MSI as a GitHub Actions artifact before submitting that
   exact artifact through SignPath's official GitHub integration;
4. require a manual SignPath approval for every production request;
5. download the returned artifact without modifying its signed contents;
6. verify a valid Authenticode chain, timestamp, expected publisher and signed
   project-owned files, then install the MSI on a clean runner and verify the
   installed payload; and
7. compute and publish SHA-256 hashes from the final signed bytes, together with
   the matching source packages and notices.

Signing changes the file, so an unsigned artifact's checksum must never be
published as the checksum of a signed release. A missing credential, approval,
signature, timestamp, expected file, validation result, source package or
final checksum stops publication. An unsigned CI artifact may remain available
for development testing only when it is clearly named and described as
unsigned.

All signed PE metadata must identify the product as Optimisarr and use the
release version. The SignPath artifact configuration must enforce those values.

## Team roles and account security

Optimisarr is currently a single-maintainer signing project. The same maintainer
holds the required roles, while each signing request remains a distinct manual
decision after the automated build:

| Role | Member | Responsibility |
| --- | --- | --- |
| Author and committer | [@Jellman86](https://github.com/Jellman86) | Maintains project source, build scripts and release workflows. |
| Reviewer | [@Jellman86](https://github.com/Jellman86) | Reviews external contributions and release changes before merge. |
| Signing approver | [@Jellman86](https://github.com/Jellman86) | Checks the tag, workflow origin, tests, artifact configuration and release evidence before approving a signing request. |

Anyone later granted a repository or SignPath role must use multi-factor
authentication for both services. Signing tokens and identifiers belong in
protected GitHub Actions secrets or variables and must not be printed in logs,
stored in release artifacts, or made available to pull-request code. The
certificate private key remains in SignPath's protected infrastructure and is
never exported to the repository or a runner.

Changes to the installer, signing workflow, artifact configuration, release
permissions or this policy receive the same review and required CI checks as
application code. Automated dependency updates cannot approve signing.

## Privacy and network transfers

Optimisarr does not contain developer analytics, advertising, crash-reporting
telemetry or a connection to an Optimisarr-operated cloud service. The project
maintainers do not receive an operator's media, credentials, configuration or
worker data through the application.

Optimisarr transfers information only to systems selected or configured by the
person installing or operating it:

- The container communicates with media servers, library managers, download
  managers and notification services only after the operator configures those
  endpoints or invokes the relevant action. Those services apply their own
  privacy policies.
- A Windows or macOS sidecar connects only to the Optimisarr server address with
  which the operator pairs it. Pairing and normal operation exchange a worker
  credential, machine name, proven media capabilities, capacity and resource
  readings, job state, diagnostics, assigned source media, candidate output,
  hashes and requested verification evidence. Media moves between the
  operator's server and paired worker; it is not sent to the Optimisarr project.
- A browser connects to the operator's Optimisarr server. Artwork and provider
  data are proxied from services the operator connected, so provider tokens are
  not exposed to the browser.
- Installation and release tooling may download pinned build dependencies from
  their documented upstream hosts. That is a maintainer build-time operation,
  not an end-user telemetry path. SignPath receives release build artifacts and
  trusted-build metadata for signing; it does not receive runtime user data.

Credentials, pairing data, job state and logs are stored on systems controlled
by the operator. Configuration exports can contain provider secrets and must be
handled accordingly. Operators decide whether their configured endpoints are
local or internet-accessible and are responsible for the privacy terms of those
services.

## Installation and removal

The Windows MSI announces and installs a machine-wide Optimisarr Sidecar
service, private runtime and media tools, a Start-menu shortcut, and the tray
companion. The service uses the Local System account and is registered for
automatic start, but an unpaired worker does not process jobs. Enabling the tray
at sign-in is a separate per-user choice.

Windows Settings can uninstall the application and remove its program files,
shortcut and service registration. Uninstall intentionally retains pairing and
scratch/media data under `%ProgramData%\Optimisarr\Sidecar` so an update or
reinstallation cannot silently destroy operator data. The installer guide
explains how to drain work before an upgrade and how to remove retained data
when it is no longer wanted.

## Verifying a Windows download

For a release described as signed, Windows PowerShell should report a valid
Authenticode signature and the release notes should identify the expected
publisher:

```powershell
$signature = Get-AuthenticodeSignature .\OptimisarrSidecar-X.Y.Z-win-x64.msi
$signature.Status
$signature.SignerCertificate.Subject
Get-FileHash .\OptimisarrSidecar-X.Y.Z-win-x64.msi -Algorithm SHA256
```

For SignPath Foundation releases, the signer identity must refer to SignPath
Foundation and the SHA-256 value must match the checksum published for that
exact release. A valid signature proves publisher identity and file integrity;
Microsoft Defender SmartScreen reputation is a separate signal and may still
warn for a new application or file.

## Incident response

If a signing token, trusted build, approval, artifact, signature or published
checksum may be compromised or incorrect, maintainers must stop new signing and
publication, preserve the relevant audit evidence, disable or rotate affected
credentials, and contact SignPath. Affected downloads must be removed or
clearly marked while the incident is investigated. If a certificate or signed
release is unsafe, maintainers will request revocation where appropriate and
publish corrected release information with the affected versions, hashes and
required user action.

## References

- [SignPath Foundation conditions for open-source projects](https://signpath.org/terms.html)
- [SignPath trusted GitHub build documentation](https://docs.signpath.io/trusted-build-systems/github)
- [SignPath MSI and nested-artifact configuration](https://docs.signpath.io/artifact-configuration/)
- [Microsoft code-signing options for Windows applications](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options)
- [Microsoft Defender SmartScreen reputation guidance](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation)
