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
  to gRPC and back. Backend gRPC status codes map to HTTP: `NotFound → 404`, `InvalidArgument → 400`.
- **Domain == persistence model.** At this scale a separate persistence DTO layer would be ceremony.
  The gRPC contract is the only separate model, mapped with **Mapperly** (compile-time, no reflection).
- **Ids: `Guid` (v7-friendly) stored as strings** so Mongo documents stay reviewer-readable. At high
  write volume a 16-byte binary representation would be preferred; the perf delta is irrelevant here.
- **Insights computed on demand** via Mongo aggregation pipelines with in-database `$lookup` joins —
  no background worker, no materialized views. Each insight is a single round trip.
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

---

## Running the application

### Prerequisites

- **.NET 10 SDK** (`10.0.201`+)
- **Docker** — required to run the app (MongoDB container) and every test tier except pure unit tests.

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

### Example requests

```bash
GET /books?limit=20&skip=0
GET /books/{id}
GET /users/{id}
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
