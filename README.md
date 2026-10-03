# Immich Duplicate Review Helper

A small, server-rendered sidecar for reviewing large Immich duplicate sets in resumable, oldest-first batches. It uses Immich's supported HTTP API and stores workflow state in SQLite; it never reads the photo filesystem or Immich database.

> **Disclaimer:** This is an unofficial community project. It is not affiliated with, endorsed by, or maintained by the Immich project or its contributors.

## MVP status

The MVP supports loading duplicate groups, persisted sortable batches, side-by-side previews, Keep/Trash/Stack decisions, Skip, Previous/Next group navigation, explicit confirmation, accurate batch progress, local resume state, failed-decision retry, keyboard controls, health endpoints, and an end-to-end restart test. A selected batch remains bounded after restart; newly discovered groups wait for a later batch.

Batch sorting supports oldest, newest, smallest group, largest group, largest potential storage saving, path, and filename. The chosen ordering is stored with the session and survives restart.

Album membership is fetched only for assets in the active batch, with bounded concurrency, and cached in local review metadata. If the API key lacks album access, review continues and displays album data as unavailable.

## Immich API key

In Immich Web, open **Account Settings → API Keys**, create a dedicated key, and grant duplicate read/delete, asset view/update/delete, album read, stack read/create, system configuration read, and user read permissions. The system-configuration permission is required to prove Immich Trash is enabled before resolving a group. Using an unrestricted key works but is not recommended.

The key remains in the backend container. Browser previews go through `/api/assets/{assetId}/preview`; browser JavaScript never receives Immich credentials.

## Run locally

Requires the .NET 10 SDK.

```bash
cp .env.example .env
set -a; source .env; set +a
dotnet run --project src/ImmichDuplicateReview
```

Open the URL printed by ASP.NET Core. Run tests with:

```bash
dotnet restore
dotnet test
```

## Run beside Immich

Set `IMMICH_URL` to Immich's Docker service URL, normally `http://immich-server:2283`, then run:

```bash
docker compose up --build -d
```

If the Immich network has a different name, change `networks.immich.name`.

## Deploy on Raspberry Pi

Use a 64-bit-capable Raspberry Pi with a 64-bit OS; a Pi 4 or 5 is recommended. Confirm that `uname -m` prints `aarch64`. This guide assumes 64-bit Raspberry Pi OS because current Docker support is moving away from 32-bit ARM. Install [Docker Engine for Raspberry Pi OS](https://docs.docker.com/engine/install/raspberry-pi-os/) and the [Docker Compose plugin](https://docs.docker.com/compose/install/linux/), then verify `docker version` and `docker compose version`.

Clone and configure the helper on the Pi:

```bash
git clone https://github.com/YOUR-USER/YOUR-REPOSITORY.git
cd YOUR-REPOSITORY
cp .env.example .env
chmod 600 .env
nano .env
```

Set `IMMICH_URL=http://immich-server:2283`, add the dedicated API key described above, and leave `DATA_PATH=/data`. Both containers must share a Docker network. Find Immich's network with `docker network ls`; if it is not `immich_default`, update `networks.immich.name` in `compose.yaml`.

Build and start the native ARM64 image:

```bash
docker compose up -d --build
docker compose logs -f
```

The runtime uses Microsoft's smaller .NET 10 chiseled image and the Compose configuration limits the helper to 384 MB of memory. If large batches or concurrent previews cause out-of-memory restarts, raise `mem_limit` in `compose.yaml` to `512m`.

Verify both liveness and dependency readiness (the `echo` keeps responses readable):

```bash
curl -s http://localhost:8080/health; echo
curl -s http://localhost:8080/ready; echo
```

The UI is available at `http://raspberrypi.local:8080`. It has no built-in user authentication, so expose it only on a trusted LAN or place it behind an authenticated reverse proxy. To update later, back up the SQLite volume first, then run:

```bash
git pull
docker compose up -d --build
```

Do not run `docker compose down -v`: it deletes the named volume containing review progress. If readiness reports `"sqlite":false` and the logs show SQLite error code 14, repair ownership without deleting the volume:

```bash
docker compose --profile maintenance run --rm repair-data
docker compose up -d --build --force-recreate
```

## Persistence and backup

Review state and active batch membership are stored at `${DATA_PATH}/reviews.db`; the Compose example uses a named volume. For a consistent backup, stop the helper and copy `reviews.db` (or back up the entire volume). Restoring that file resumes the same bounded batch and position.

If `/ready` reports `"sqlite":false` and logs SQLite error code 14 after upgrading an older deployment, repair the existing volume once with:

```bash
docker compose --profile maintenance run --rm repair-data
docker compose restart duplicate-review-helper
```

## Safety

Every destructive proposal is displayed before execution. Trash decisions use Immich's duplicate resolver and are refused unless the helper confirms Immich Trash is enabled. The helper never requests permanent deletion, empties Trash, modifies source files, or chooses assets automatically. Failed API operations are recorded and never marked reviewed; the saved choices are restored for review and explicit confirmation before retrying.

Operational endpoints are `GET /health` and `GET /ready`; readiness requires both SQLite and authenticated Immich access.

`DEFAULT_BATCH_SIZE` controls the initially selected batch size and must be `50`, `100`, `250`, or `500`. Dependency failures return a retryable error without exposing upstream details or credentials. The process remains live when SQLite initialization fails so orchestration can distinguish `/health` from `/ready`.
