# Immich API Integration

Validated against the current official Immich server source/OpenAPI behavior in October 2026:

| Capability | Request | Notes |
| --- | --- | --- |
| Duplicates | `GET /api/duplicates` | Maps Immich DTOs into internal groups/assets. |
| Preview | `GET /api/assets/{id}/thumbnail?size=preview` | Streamed through the helper; `x-api-key` stays server-side. |
| Resolve | `POST /api/duplicates/resolve` | Sends every asset in `keepAssetIds` or `trashAssetIds`. |
| Album membership | `GET /api/albums?assetId={id}` | Enriches active-batch assets with album names; optional failures do not block review. |
| Find stack | `GET /api/stacks?primaryAssetId={id}` | Makes stack creation idempotent when the desired stack already exists. |
| Create stack | `POST /api/stacks` | Sends ordered `assetIds`; the first asset becomes primary. |
| Trash safety | `GET /api/config` | Resolve is refused unless the user-visible `trash.enabled` setting is true. |
| Readiness | `GET /api/users/me`, `GET /api/duplicates`, `GET /api/config` | Verifies authentication, duplicate access, configuration access, and safe Trash behavior. |

Authentication uses the `x-api-key` request header. Immich-specific request/response records remain private to `ImmichClient`.

## Limitations

- Immich returns duplicate groups as one collection; ordered batch membership is persisted locally after synchronization.
- Album membership is not present in the duplicate response, so active-batch assets require bounded per-asset album lookups. Results are cached in SQLite metadata.
- Resolve actions use persisted started/completed checkpoints. After a lost response, the helper reloads duplicates and only repeats resolve when the group is still present. If it has disappeared, the outcome is marked ambiguous for manual verification rather than assumed successful. A retry after stack failure does not repeat a successful duplicate resolution, and a matching existing stack is detected before creation.
- If system configuration cannot be read, trash actions fail closed. This avoids Immich behavior that can permanently delete when its Trash feature is disabled.

No database fallback is used for missing API capabilities.

## Pinned contract test

The opt-in Testcontainers contract test starts Immich `v3.2.0` with its pinned Valkey and PostgreSQL images, creates an isolated administrator and limited API key through supported HTTP endpoints, and verifies duplicate reads plus the Trash configuration contract. Docker is required and the first run downloads large images:

```bash
RUN_IMMICH_CONTRACT_TESTS=1 dotnet test --filter Category=ImmichContract
```

Ordinary `dotnet test` runs report this test as skipped and never start Docker. Upgrade the pinned Immich tag deliberately in a separate pull request after the contract suite passes.
The **Immich contract** GitHub Actions workflow runs the same suite on demand without adding the large container stack to every pull request.
