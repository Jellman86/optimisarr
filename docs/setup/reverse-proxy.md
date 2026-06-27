# Running behind a reverse proxy

Optimisarr is a single HTTP service on port `8787`. It can sit behind any reverse
proxy. The one thing you **must** get right is **WebSocket upgrades**: the live
Queue progress, CPU/GPU graphs, and job updates use a SignalR hub at
`/hubs/jobs`, which upgrades to a WebSocket. If the proxy doesn't forward the
`Upgrade`/`Connection` headers, the UI still works but falls back to slower
polling and live updates feel laggy.

Put Optimisarr behind your proxy's authentication (or a trusted network) if it
is reachable from outside. You can also set `OPTIMISARR_ADMIN_TOKEN` for a
built-in bearer-token backstop on the UI, API, and SignalR hub, but it is not a
replacement for TLS, proxy authentication, IP allow-lists, or your normal
internet-facing access controls.

Assume the container is reachable from the proxy as `http://optimisarr:8787`
(Docker network) or `http://127.0.0.1:8787` (host). Adjust the upstream to match.

## Built-in admin token

Set `OPTIMISARR_ADMIN_TOKEN` on the container to require the same token in the
web UI and on API calls. Health probes remain unauthenticated:

- `GET /api/health`
- `GET /api/ready`
- `GET /api/auth/status`

API clients should send:

```bash
curl -H "Authorization: Bearer change-this-long-random-token" \
  https://optimisarr.example.com/api/settings
```

Browser media previews use request URLs that include the token as `access_token`
because `<video>` and `<img>` requests cannot attach bearer headers. Prefer
HTTPS and avoid logging query strings at the proxy if you enable remote previews.

## Caddy

Caddy proxies WebSockets automatically — no extra configuration needed.

```caddyfile
optimisarr.example.com {
    reverse_proxy optimisarr:8787
}
```

## Nginx

The `Upgrade`/`Connection` headers are what keep the SignalR WebSocket alive.

```nginx
server {
    listen 443 ssl;
    server_name optimisarr.example.com;

    # ssl_certificate / ssl_certificate_key ...

    location / {
        proxy_pass http://127.0.0.1:8787;
        proxy_http_version 1.1;

        # Required for the SignalR (/hubs/jobs) WebSocket.
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";

        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Don't time out the long-lived hub connection.
        proxy_read_timeout 3600s;
    }
}
```

## Traefik (Docker labels)

Traefik forwards WebSocket upgrades automatically. Add labels to the Optimisarr
service:

```yaml
services:
  optimisarr:
    image: ghcr.io/jellman86/optimisarr:latest
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.optimisarr.rule=Host(`optimisarr.example.com`)"
      - "traefik.http.routers.optimisarr.entrypoints=websecure"
      - "traefik.http.routers.optimisarr.tls.certresolver=myresolver"
      - "traefik.http.services.optimisarr.loadbalancer.server.port=8787"
```

## Subpath hosting

Optimisarr expects to be served from the **root of its hostname** (e.g.
`https://optimisarr.example.com/`), not a subpath like `/optimisarr/`. Its
assets and API are referenced from `/`. Use a dedicated host or subdomain rather
than a path prefix.
