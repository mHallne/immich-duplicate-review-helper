# Immich Duplicate Review Helper

A small, server-rendered sidecar for reviewing large Immich duplicate sets in resumable, oldest-first batches. It uses Immich's supported HTTP API and stores workflow state in SQLite; it never reads the photo filesystem or Immich database.

> **Disclaimer:** This is an unofficial community project. It is not affiliated with, endorsed by, or maintained by the Immich project or its contributors.

## Current milestone

The working MVP supports loading duplicate groups, 100-item oldest-first batches, side-by-side previews, Keep/Trash decisions, Skip, explicit confirmation, local resume state, keyboard controls, health endpoints, and an end-to-end restart test. Stack decisions are validated by the model but execution is not yet enabled in the UI; see [API integration notes](docs/immich-api.md).

## Immich API key

In Immich Web, open **Account Settings → API Keys**, create a dedicated key, and grant duplicate read/delete, asset view/delete, system configuration read, and user read permissions. The system-configuration permission is required to prove Immich Trash is enabled before resolving a group. Using an unrestricted key works but is not recommended.

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
docker compose -f docker-compose.example.yml up --build -d
```

If the Immich network has a different name, change `networks.immich.name`.

## Persistence and backup

Review state is stored at `${DATA_PATH}/reviews.db`; the Compose example uses a named volume. For a consistent backup, stop the helper and copy `reviews.db` (or back up the entire volume). Restoring that file resumes the active session.

## Safety

Every destructive proposal is displayed before execution. Trash decisions use Immich's duplicate resolver and are refused unless the helper confirms Immich Trash is enabled. The helper never requests permanent deletion, empties Trash, modifies source files, or chooses assets automatically. Failed API operations are recorded and never marked reviewed.

Operational endpoints are `GET /health` and `GET /ready`; readiness requires both SQLite and authenticated Immich access.
