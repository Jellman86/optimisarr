# Security policy

## Supported versions

Security fixes are applied to the current `main` release and current `dev` build.
Older images are unsupported.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Contact the repository
owner privately through GitHub and include reproduction steps, affected version,
impact, and any suggested mitigation. Acknowledgement is targeted within seven
days; a public advisory will be coordinated after a fix is available.

## Deployment boundary

Optimisarr is intended for trusted self-hosted networks. Do not publish its port
directly to the internet. Put any remote access behind an authenticated reverse
proxy and keep Docker, the host, and the Optimisarr image updated.

The web UI exposes configuration, queue, replacement, rollback, and backup
operations to anyone who can reach it. Treat `8787` like an administrative
interface: bind it only where you intend to use it, restrict firewall/VPN access,
and prefer a dedicated authenticated hostname when publishing through a reverse
proxy.

Configuration exports contain provider tokens/API keys in plain text so a backup
can restore working integrations. Store exported JSON with the same care as
`/config/optimisarr.db`, and do not commit or share it.
