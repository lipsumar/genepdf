# Deploying the demo

The demo runs on a Docker host behind Traefik, deployed by a GitHub Action on every push to `master`. The general
procedure (keys, secrets, checks) lives in the private infra docs, not in this repo. This file only covers what is
specific to GenePdf.

The demo's URL is deliberately not written anywhere in this repo: it lives in `.env` on the server.

| | |
|---|---|
| Project name | `genepdf` → `/srv/genepdf` |
| Stack | One `app` container: ASP.NET (.NET 10, Alpine) + Chromium, port 8080, uid 10001, ~1.3 GB image |
| Memory | `mem_limit: 1g` (Chromium + up to 2 concurrent renders) |
| Volume | None. Templates are baked into the image; the demo is read-only (`GENEPDF_READONLY=true`): edits are rendered but never saved |
| Healthcheck | `GET /health` |

## Secrets (`/srv/genepdf/.env`)

| Variable | |
|---|---|
| `GENEPDF_HOST` | **Required.** The demo's hostname. |
| `BASIC_AUTH_USERS` | **Required.** Traefik basic auth, htpasswd format: `htpasswd -nbB demo '<password>'`. Put the value in **single quotes** in `.env` so compose doesn't interpolate the `$` in the hash. |
| `GENEPDF_READONLY` | Optional, defaults to `true` |

Basic auth is not optional: the studio sends arbitrary HTML to Chromium. Network access from Chromium is blocked and
JavaScript is disabled, but the demo should still not be an open rendering service.

## Checks after deploy

```sh
HOST=…   # the value of GENEPDF_HOST
curl -sI https://$HOST/health            # 401 without credentials (auth works)
curl -s -u demo:… https://$HOST/health   # {"status":"ok"}
curl -s -u demo:… -X POST --data-binary @templates/documents/invoice-fr/1.0/samples/default.json \
  https://$HOST/api/generate/invoice-fr/1.0 -o /tmp/f.pdf && scripts/verify-pdfa.sh /tmp/f.pdf
```
