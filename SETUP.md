# SETUP

Instructions for running BoardSync locally, from a clean clone. Written for anyone picking this project up cold — assume less context than `README.md`.

## Prerequisites

- **Docker** and **Docker Compose v2** (`docker compose version`). This is the only required path.
- **.NET 10 SDK**, only if you want to run tests, migrations, or `dotnet format` outside the containers. Not required for `docker compose up`.
- **Node.js 24+ and pnpm 10+**, only if you want to run the client outside the container.

Nothing else. No external accounts, no third-party API keys — this project has none.

## First-time setup

1. Copy the environment file and fill in real values:

   ```bash
   cp .env.example .env
   ```

   Replace every `REPLACE_ME` in `.env`. For local development, generate a random Postgres password:

   ```bash
   openssl rand -base64 24 | tr -d '/+=' | cut -c1-32
   ```

   Put the **same** value in both `POSTGRES_PASSWORD` and inside `ConnectionStrings__Default`.

2. Bring up the stack:

   ```bash
   docker compose up
   ```

   First run pulls three images (`postgres:17-alpine`, `mcr.microsoft.com/dotnet/sdk:10.0`, `node:24-slim`) and restores/installs dependencies fresh into named volumes. This takes a few minutes. Subsequent runs are fast — dependencies persist in `api-nuget-cache` and `client-node-modules` volumes, and don't re-download unless the lockfile/csproj changes.

3. Open the client: **http://localhost:5173**

   You should see the seeded board name, "BoardSync Demo", fetched live from Postgres. If you see "Could not load the board: Failed to fetch" in the browser but `curl http://localhost:5080/api/board` works fine from the host, that's CORS — check `CorsOrigin` in `.env` matches the client's actual origin (`http://localhost:5173` by default). `curl` bypasses CORS entirely, so it will not reveal this class of bug; only a real browser does.

## What each service does

| Service | Image | Port | Purpose |
|---|---|---|---|
| `db` | `postgres:17-alpine` | `5432` | Database. Data persists in the `db-data` volume. |
| `api` | `mcr.microsoft.com/dotnet/sdk:10.0` | `5080` → container `8080` | Runs `dotnet watch run`. Applies EF Core migrations and seeds the demo board on startup. Hot-reloads on source changes. |
| `client` | `node:24-slim` | `5173` | Runs the Vite dev server. Hot-reloads on source changes. |

All three are wired by `docker-compose.yml` and configured entirely from `.env`.

## Environment variables

| Variable | Used by | Meaning |
|---|---|---|
| `POSTGRES_USER` | `db` | Database role name |
| `POSTGRES_PASSWORD` | `db`, `api` (via `ConnectionStrings__Default`) | Database password. Self-generated, no external account needed. |
| `POSTGRES_DB` | `db` | Database name |
| `POSTGRES_PORT` | `db` | Host port Postgres is exposed on (default `5432`) |
| `API_PORT` | `api` | Host port the API is exposed on (default `5080`) |
| `ASPNETCORE_ENVIRONMENT` | `api` | `Development` locally. Controls things like the OpenAPI endpoint and, from phase 9 onward, whether the concurrency-demo delay is allowed to run at all. |
| `ConnectionStrings__Default` | `api` | Full Postgres connection string. Host must be the compose service name `db`, not `localhost` — the API reaches Postgres over the Docker network, not the host's port mapping. |
| `CorsOrigin` | `api` | The browser origin allowed to call the API. Must exactly match where the client is actually served from. Only needed in dev, where client and API run on different ports; production puts both behind one nginx origin and needs no CORS policy at all. |
| `CLIENT_PORT` | `client` | Host port the client dev server is exposed on (default `5173`) |
| `VITE_API_URL` | `client` (baked into the browser bundle) | The API's browser-facing URL. Must be reachable from your machine, not just from inside the Docker network — `http://localhost:5080`, never a service name like `http://api:8080`. |
| `ConcurrencyDemo__ArtificialDelayMs` | `api` | Dev-only latency (ms) injected into `MoveCard` between position resolution and save, to widen the optimistic-concurrency race window for a live demo. Default `0`. The API refuses to start if this is non-zero and `ASPNETCORE_ENVIRONMENT` isn't `Development`. |

Every variable above exists in both `.env.example` (placeholders) and `.env` (working values). If you introduce a new one while developing, add it to both files in the same commit — never leave one undocumented.

## Running tests

```bash
# From the server/ directory
dotnet test BoardSync.Tests/BoardSync.Tests.csproj
```

Requires a local Docker daemon — the test suite spins up a real, throwaway Postgres container via Testcontainers for every test run (no mocking of the database). First run pulls the `postgres:17-alpine` image if it isn't already cached.

Client tests (Vitest) are introduced starting phase 3; this section will grow a client test command when that lands.

## Linting and formatting

```bash
# Server, from server/
dotnet format BoardSync.slnx --verify-no-changes   # check only
dotnet format BoardSync.slnx                        # apply fixes

# Client, from client/
pnpm lint                                            # ESLint
pnpm format:check                                    # Prettier, check only
pnpm format                                           # Prettier, apply fixes
```

## Database migrations

Migrations are managed with `dotnet-ef`. Install it once if you don't have it:

```bash
dotnet tool install --global dotnet-ef
export PATH="$PATH:$HOME/.dotnet/tools"   # if not already on PATH
```

The project ships a design-time factory (`BoardSync.Api/Data/DesignTimeDbContextFactory.cs`), so `dotnet ef` commands work without a running app or a live connection string:

```bash
# From server/BoardSync.Api
dotnet ef migrations add <Name> --output-dir Data/Migrations
dotnet ef migrations remove
```

Migrations apply automatically when the `api` container starts (`db.Database.Migrate()` in `Program.cs`) — there is no separate manual migration step for local development. To apply migrations against a database outside the compose stack:

```bash
ConnectionStrings__Default="Host=localhost;Port=5432;Database=boardsync;Username=boardsync;Password=<your-password>" \
  dotnet ef database update
```

## Running the API natively, without Docker

Only needed if you specifically want to debug the API outside a container (e.g. attach a native debugger). The client is set up to always run the same way (`pnpm dev`), Docker or not.

```bash
# From server/
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=boardsync;Username=boardsync;Password=<your-password>"
export CorsOrigin="http://localhost:5173"
export ASPNETCORE_ENVIRONMENT=Development
dotnet watch run --project BoardSync.Api
```

Postgres must already be reachable at `localhost:5432` — either leave `docker compose up db` running and stop only the `api` container, or point at your own local Postgres 17 instance.

## Resetting everything

```bash
docker compose down -v
```

Drops all containers **and** named volumes — including the database. Nothing here is meant to hold data you care about keeping; the seeded demo board regenerates automatically on the next `docker compose up`.
