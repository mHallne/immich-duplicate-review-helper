# Immich API Integration

Validated against the current official Immich server source/OpenAPI behavior in September 2026:

| Capability | Request | Notes |
| --- | --- | --- |
| Duplicates | `GET /api/duplicates` | Maps Immich DTOs into internal groups/assets. |
| Preview | `GET /api/assets/{id}/thumbnail?size=preview` | Streamed through the helper; `x-api-key` stays server-side. |
| Resolve | `POST /api/duplicates/resolve` | Sends every asset in `keepAssetIds` or `trashAssetIds`. |
| Trash safety | `GET /api/system-config` | Resolve is refused unless `trash.enabled` is true. |
| Readiness auth | `GET /api/users/me` | Verifies reachability and API-key authentication. |

Authentication uses the `x-api-key` request header. Immich-specific request/response records remain private to `ImmichClient`.

## Limitations

- Immich returns duplicate groups as one collection; batching is local after synchronization.
- Album membership is not present in the duplicate response and is not enriched in this milestone.
- Stack execution is not implemented yet. It will require verified create/update stack semantics and ordering relative to duplicate resolution.
- If system configuration cannot be read, trash actions fail closed. This avoids Immich behavior that can permanently delete when its Trash feature is disabled.

No database fallback is used for missing API capabilities.
