# Architecture

The application is one ASP.NET Core deployment organized by vertical feature slices:

- `Features/Batches` owns duplicate-group models and oldest-first batch creation.
- `Features/Reviews` owns decisions, SQLite state, confirmation, skip/resume, API endpoints, and server-rendered review pages.
- `Features/Previews` owns the authenticated image proxy.
- `Features/Health` owns liveness and readiness.
- `Integrations/Immich` contains the only Immich-specific HTTP DTOs and authentication behavior.

Each feature keeps its endpoint, workflow, validation, and persistence operations together. Tests mirror feature paths under `tests/ImmichDuplicateReview.Tests`. There are no domain/application/infrastructure project layers, mediator pipeline, generic repositories, or direct PostgreSQL access.

SQLite initialization is intentionally idempotent and runs at startup. Review state changes occur only after Immich reports success. A failed call records `failed`, preserves its decision JSON, and can be retried. Already-reviewed confirmations return without repeating the Immich operation.
