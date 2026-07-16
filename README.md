# BookLibrary

A library management API that answers a librarian's key operational questions — most borrowed
books, most active borrowers, reading pace, and borrowing patterns — over a clean, layered
architecture with **mandatory gRPC** between the layers and **MongoDB** for storage.

Built on **.NET 10** and orchestrated with **.NET Aspire**.

---

## Architecture

```
        HTTP (REST + Scalar)            gRPC / HTTP2              MongoDB driver
Client ───────────────────▶  Api  ───────────────────▶  Catalog ───────────────▶  MongoDB
                          (REST edge)              (domain + persistence)         (container)
```

Two independent processes, exactly as the assignment's "at least two layers" requires — kept as
separate processes so the gRPC boundary is real, not decorative:

| Project | Role |
| --- | --- |
| **BookLibrary.Api** | REST facade. HTTP concerns only: binding, validation surfacing, Scalar docs. A gRPC *client* of Catalog. |
| **BookLibrary.Catalog** | gRPC *server*. All business logic, insight aggregations and Mongo access. |
| **BookLibrary.Contracts** | The `.proto` contract; generates the gRPC server base + typed client. |
| **BookLibrary.Seeder** | One-shot, idempotent sample-data loader. Runs once, then exits. |
| **BookLibrary.AppHost** | Aspire orchestration: Mongo container + the three projects, wired with service discovery. |
| **BookLibrary.ServiceDefaults** | Shared OpenTelemetry, health checks, resilience, service discovery. |
| **BookLibrary.WarmUp** (+ `.Tests`) | The warm-up exercises (separate from the main task). |

### Key design decisions

- **gRPC as a true process boundary.** The REST edge holds no business logic; it translates HTTP
  to gRPC and back. Backend gRPC status codes map to HTTP: `NotFound → 404`, `InvalidArgument → 400`,
  `FailedPrecondition`/`AlreadyExists → 409` (state conflicts — see
  [Business rules](#business-rules-what-the-numbers-mean)).
- **Domain == persistence model.** At this scale a separate persistence DTO layer would be ceremony.
  The gRPC contract is the only separate model, mapped with **Mapperly** (compile-time, no reflection).
- **Ids: `Guid` (v7-friendly) stored as strings** so Mongo documents stay reviewer-readable. At high
  write volume a 16-byte binary representation would be preferred; the perf delta is irrelevant here.
- **Insights computed on demand** via Mongo aggregation pipelines with in-database `$lookup` joins —
  no background worker, no materialized views. Each insight is a single round trip.
- **Insight responses cached at the REST edge** with **`HybridCache`** — an in-memory tier today;
  adding a distributed L2 (e.g. Redis) later is a registration-only change with no call-site edits.
  Expiry is TTL-based and tunable via the `CatalogCache` config section; every entry is tagged
  `insights`, and every successful write (book/user/loan create, update, delete) evicts the whole
  tag via `InsightCacheInvalidator` — a new loan alone can move most-borrowed, top-borrowers *and*
  co-borrowed at once, so tag-based bulk eviction is simpler than tracking per-entry dependencies.
  Top-borrowers uses a shorter TTL because an omitted window drifts with `now`. **Known limitation:**
  the cache is in-memory-only, so with more than one Api replica each replica evicts only its own
  copy; the TTLs bound how stale another replica's cache can get. Adding a distributed L2 makes
  invalidation cross-replica automatically — no call-site changes needed.
- **Structured logging** via `[LoggerMessage]` source generators; traces/metrics/logs flow to the
  Aspire dashboard and correlate across the REST→gRPC hop.

### Data model

Three MongoDB collections — `Books`, `Users` and `Loans` — in a single `library` database, where a
`Loan` is the central fact linking a user to a book over time. See
**[docs/data-schema.md](docs/data-schema.md)** for the full schema: fields, document examples,
indexes and the modelling decisions behind them.

---

## Business rules (what the numbers mean)

These are deliberate product decisions — documented here because they change what each answer means.

- **Counted borrow (applies to every insight).** A loan counts as a genuine borrow **unless it was
  returned in under 1 day** — sub-day returns are treated as mistakes (wrong book, over-borrowed for
  a weekend) and dropped everywhere, so the insights stay reliable. The boundary is *duration-based*
  (not calendar-day) to avoid midnight/timezone edges. **Open loans still count** — the book left
  the shelf, it just isn't back yet.
- **Most borrowed books** — ranked by counted-borrow count (5 borrows by one user == 5 by five users).
- **Top borrowers** — counted borrows per user with `BorrowedAt ∈ [from, to)` (half-open window).
  When no window is given, defaults to **year-to-date** (`[Jan 1 this year UTC, now)`).
- **Reading pace** — `PageCount / (ReturnedAt − BorrowedAt).TotalDays` for the most recent *completed,
  counted* loan. Undefined (and explained) for open loans, sub-1-day returns, or non-positive page counts.
- **Co-borrowed books** — users who borrowed X → their other counted borrows → **excluding X** →
  ranked by **distinct co-borrower count**, so one heavy reader can't skew a title.

Write rules (the CRUD surface, layered on top of the one-book-one-copy model — see
[docs/data-schema.md](docs/data-schema.md#design-notes)):

- **A book cannot be borrowed while already on loan.** Enforced both as a friendly read-check
  (`409 Conflict`) and, under concurrency, by a unique database index — see
  [docs/data-schema.md](docs/data-schema.md#design-notes).
- **Deleting a book force-closes any open loan on it** (the "lost book" flow — the physical copy is
  gone, so the loan can never be closed by a return) and reports how many loans it closed.
- **Deleting a user is refused (`409 Conflict`) while they hold open loans** — unlike book delete,
  because the books they hold are still physically out in the world and must come back first. This
  asymmetry is deliberate, not an inconsistency.
- **A loan can only be closed (returned), never edited or deleted.** `POST /loans/{id}/return` is
  the sole legal mutation; loan history is immutable once created.
- **Renaming a user does not rewrite past loans' snapshotted names** — `Loan.UserName` records the
  name as it was at borrow time (see [docs/data-schema.md](docs/data-schema.md#design-notes)).

---

## Running the application

### Prerequisites

- **.NET 10 SDK** (`10.0.201`+)
- **Docker** — required to run the app (MongoDB container) and every test tier except pure unit tests.
  Podman works too — see [Using Podman instead of Docker](#using-podman-instead-of-docker) below.
- **A trusted local HTTPS dev certificate** — Aspire's dashboard and resource-to-resource calls run
  over HTTPS/h2c; if the cert isn't trusted, orchestration fails to start. See
  [Trusting the HTTPS dev certificate](#trusting-the-https-dev-certificate) below if you hit this.

#### Using Podman instead of Docker

Aspire shells out to `docker` by default. If your machine has Podman instead, tell Aspire to use it
by setting `ASPIRE_CONTAINER_RUNTIME=podman` before running the AppHost:

```bash
# macOS/Linux
export ASPIRE_CONTAINER_RUNTIME=podman

# Windows (PowerShell), persists for future sessions
[System.Environment]::SetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME", "podman", "User")
```

Also make sure the Podman machine/socket is running (`podman machine start` on macOS/Windows) before
`dotnet run --project BookLibrary.AppHost` — Aspire talks to it the same way it would talk to Docker.

#### Trusting the HTTPS dev certificate

Aspire's dashboard and the inter-resource HTTPS endpoints rely on the standard ASP.NET Core
localhost dev certificate. If it isn't trusted, `dotnet run --project BookLibrary.AppHost` fails
during startup (or the dashboard/resources fail their health checks) instead of running cleanly.
Aspire's own docs point at the `aspire` CLI (`aspire run`, which trusts the cert for you) — but you
don't need that CLI installed; the plain .NET SDK can trust the same certificate directly:

```bash
dotnet dev-certs https --trust
```

- **Windows/macOS**: this installs the cert into the OS trust store and just works; you'll get a
  one-time confirmation prompt.
- **Linux**: there's no OS-wide trust store integration, so `--trust` alone isn't enough. Export the
  cert and trust it in your browser/distro's store manually, e.g. on Ubuntu/Debian:
  ```bash
  dotnet dev-certs https -ep ~/aspnet-dev-cert.crt --format PEM
  sudo cp ~/aspnet-dev-cert.crt /usr/local/share/ca-certificates/aspnet-dev-cert.crt
  sudo update-ca-certificates
  ```
  then re-import that same cert into your browser's certificate store if the browser doesn't read
  the system one.
- Running inside **WSL2**: the cert must be generated and trusted *inside* WSL2 (it's a separate
  filesystem/cert store from Windows), even though the AppHost eventually opens a Windows browser.

Run `dotnet dev-certs https --check --trust` afterwards to confirm it's picked up before retrying
`dotnet run --project BookLibrary.AppHost`.

### Start everything

```bash
dotnet run --project BookLibrary.AppHost
```

The Aspire dashboard opens automatically. From it you can reach:

- **Scalar API reference** — the `api` resource → `/scalar/v1` (the API root redirects there).
- **Structured logs, traces and metrics** for every resource, including the REST→gRPC→Mongo trace.

The seeder populates deterministic sample data on first run (it skips if the catalog is already
populated) from embedded JSON files in `BookLibrary.Seeder/Data/` — a curated real-world catalogue of
100+ books (each with a publication year), 15+ users, and a few hundred loans, hand-shaped to cover
every insight and every reading-pace branch. See [`docs/data-schema.md`](docs/data-schema.md#sample-data).

### Cursor pagination

Every list endpoint (`/books`, `/users`, `/loans`) is paginated the same way: `?limit=&cursor=`,
returning `{ "items": [...], "nextCursor": "..." }`. `nextCursor` is `null` on the last page.
**Cursors are opaque** — created and interpreted by the backend only. Clients must pass a cursor
back exactly as received and never construct or parse one themselves.

```bash
GET /books?limit=20                 # first page
GET /books?limit=20&cursor={cursor} # next page, using the previous response's nextCursor
```

### Example requests

```bash
# Books
GET    /books?limit=20
GET    /books/{id}
POST   /books                 { "title", "author", "pageCount", "year" }
DELETE /books/{id}            # force-closes any open loan (lost-book flow)

# Users
GET    /users?limit=20
GET    /users/{id}
POST   /users                 { "name" }
PUT    /users/{id}            { "name" }   # full replacement; name is the only mutable field
DELETE /users/{id}            # 409 while the user holds open loans

# Loans
GET    /loans?limit=20&userId={userId}&bookId={bookId}&openOnly=true
GET    /loans/{id}
POST   /loans                 { "bookId", "userId", "borrowedAt"? }   # borrowedAt defaults to now
POST   /loans/{id}/return     { "returnedAt"? }                       # empty/no body -> now

# Insights
GET /insights/most-borrowed?limit=10&from=2026-01-01&to=2026-12-31
GET /insights/top-borrowers?limit=10&from=2026-01-01&to=2026-07-01
GET /insights/reading-pace?userId={userId}&bookId={bookId}
GET /insights/co-borrowed/{bookId}?limit=10
```

---

## Tests

All tests run through `dotnet test` (Microsoft.Testing.Platform) using **TUnit**.

```bash
dotnet test                                                  # everything (needs Docker)
dotnet test --project BookLibrary.Catalog.Tests              # unit + integration + functional
dotnet test --project BookLibrary.SystemTests                # full-system flows
```

| Tier | Where | Needs Docker | What it covers |
| --- | --- | :---: | --- |
| **Unit** | `Catalog.Tests/Unit` | no | Reading-pace branches, the counted-borrow rule, domain→contract mapping. |
| **Integration** | `Catalog.Tests/Integration` | yes | Aggregation pipelines run directly against a real Mongo (Testcontainers). |
| **Functional** | `Catalog.Tests/Functional` | yes | The gRPC service surface (validation, status codes, mapping) over an in-memory host + Mongo. |
| **System** | `SystemTests` | yes | Complete HTTP→gRPC→Mongo user flows via `Aspire.Hosting.Testing`. |

Run only the Docker-free unit tests:

```bash
dotnet test --project BookLibrary.Catalog.Tests --treenode-filter "/*/*.Unit/*/*"
```

> **Docker is a hard prerequisite** for the integration, functional and system tiers. With Docker
> stopped, only the unit tier (and the warm-up tests) will run.

### Warm-up exercises

The starter tasks live in `BookLibrary.WarmUp` with tests in `BookLibrary.WarmUp.Tests`:

```bash
dotnet test --project BookLibrary.WarmUp.Tests
```
