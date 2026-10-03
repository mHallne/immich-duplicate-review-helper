# Repository Guidelines

## Project Structure

This is one .NET 10 ASP.NET Core application organized by vertical slice. Production code lives in `src/ImmichDuplicateReview/`. Add behavior under the owning feature, such as `Features/Batches`, `Features/Reviews`, `Features/Previews`, or `Features/Health`. Keep Immich HTTP details in `Integrations/Immich`; do not add layered Domain/Application/Infrastructure projects. Tests mirror feature paths under `tests/ImmichDuplicateReview.Tests/`. Operational documentation belongs in `docs/`.

## Build, Test, and Run

- `dotnet restore` downloads pinned dependencies.
- `dotnet build ImmichDuplicateReview.slnx` compiles with nullable references and warnings as errors.
- `dotnet test` runs unit, SQLite integration, HTTP, and restart-flow tests.
- `dotnet run --project src/ImmichDuplicateReview` starts the local server.
- `docker compose up --build` runs the containerized helper.

## TDD and Coding Conventions

Write a focused failing test before production code, implement the minimum behavior, then refactor while green. Use real temporary SQLite databases; do not mock SQLite internals. Test Immich requests at the HTTP boundary and use fakes only at application seams.

Use four-space indentation, file-scoped namespaces, nullable reference types, and `PascalCase` for public members/types. Prefer small records for request/response values. Keep endpoint, workflow, validation, and persistence code near its feature. Avoid MediatR, AutoMapper, generic repositories, and speculative abstractions.

## Safety and Pull Requests

Never access Immich PostgreSQL or photo files. Never expose `IMMICH_API_KEY`, log authentication headers, permanently delete assets, or bypass explicit confirmation. Trash behavior must fail closed if Immich Trash cannot be verified as enabled.

Use concise imperative commits, optionally Conventional Commits (`feat: add review proposal`). Pull requests should describe behavior, safety implications, configuration changes, and exact verification commands. Include screenshots for UI changes and link related issues.
