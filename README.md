# BookLibrary

A library management API that answers a librarian's key questions: which books are borrowed most,
who borrows the most, how fast does a user read, and what else do readers of a given book borrow.

Built with **.NET 10**, orchestrated with **.NET Aspire**, with a **mandatory gRPC** boundary
between the layers and **MongoDB** for storage.

---

## Architecture

```
        HTTP (REST + Scalar)            gRPC / HTTP2              MongoDB driver
Client ───────────────────▶  Api  ───────────────────▶  Catalog ───────────────▶  MongoDB
                          (REST edge)              (domain + persistence)         (container)
```

Two separate processes, so the gRPC boundary required by the assignment is real, not decorative.

| Project | Role |
| --- | --- |
| **BookLibrary.Api** | REST facade. HTTP concerns only. A gRPC *client* of Catalog. |
| **BookLibrary.Catalog** | gRPC *server*. All business logic, insight aggregations, Mongo access. |
| **BookLibrary.Contracts** | The `.proto` contract; generates the gRPC server base and typed client. |
| **BookLibrary.Seeder** | One-shot, idempotent sample-data loader. Runs once, then exits. |
| **BookLibrary.AppHost** | Aspire orchestration: Mongo container plus the three services. |
| **BookLibrary.ServiceDefaults** | Shared OpenTelemetry, health checks, resilience, service discovery. |
| **BookLibrary.WarmUp** (+ `.Tests`) | The warm-up exercises, separate from the main task. |

### Key design decisions

- **All business logic lives in Catalog.** The REST edge only translates HTTP to gRPC and back.
  Backend status codes map to HTTP: `NotFound → 404`, `InvalidArgument → 400`,
  `FailedPrecondition` / `AlreadyExists → 409`.
- **Domain model == persistence model.** A separate persistence DTO layer would be ceremony at
  this scale. The gRPC contract is the only separate model, mapped with **Mapperly**
  (compile-time source generation, no reflection).
- **Ids are `Guid` (v7), stored as strings** so Mongo documents stay human-readable. At high
  write volume a 16-byte binary representation would be the better choice.
- **Insights are computed on demand** with Mongo aggregation pipelines — no background workers,
  no precomputed views. Each insight is a single database round trip. See
  [How this scales](#how-this-scales) for when and how that would change.
- **Insight responses are cached at the REST edge** with `HybridCache` (in-memory today).
  Every successful write evicts all cached insights via one shared tag. This deliberately
  over-invalidates — a new loan alone can change three insights at once, so one bulk eviction is
  simpler and safer than tracking per-entry dependencies. TTLs are configurable via the
  `CatalogCache` config section.
- **Structured logging** via `[LoggerMessage]` source generators; logs, traces and metrics flow
  to the Aspire dashboard and correlate across the REST → gRPC hop.

### Data model

Three collections — `Books`, `Users`, `Loans` — in one `library` database. A `Loan` is the
central fact linking a user to a book over time. Full schema, indexes and modelling decisions:
**[docs/data-schema.md](docs/data-schema.md)**.

---

## Business rules

These are deliberate product decisions. They change what the numbers mean, so they are spelled
out here.

**The counted-borrow rule (applies to every insight).** A loan returned in under 1 day is treated
as a mistake — wrong book, changed mind — and excluded from all insights. The boundary is a
duration (24 hours), not a calendar day, to avoid midnight and timezone edge cases. Open loans
**do** count: the book left the shelf, it just isn't back yet.

- **Most borrowed books** — ranked by counted borrows. Five borrows by one user count the same as
  five borrows by five users.
- **Top borrowers** — counted borrows per user with `BorrowedAt` inside a half-open `[from, to)`
  window. Defaults to year-to-date when no window is given.
- **Reading pace** — `pages / days on loan` for the user's most recent completed, counted loan of
  that book. When no estimate is possible (loan still open, sub-day return, no page count), the
  response says so and explains why.
- **Co-borrowed books** — for readers of book X: their other counted borrows, excluding X, ranked
  by **distinct co-borrowers** so one heavy reader can't skew a title.

Write rules (the model is one book document = one physical copy):

- **A book on loan cannot be borrowed again.** Enforced by a friendly check (`409 Conflict`) and,
  under concurrency, by a unique partial index in Mongo — the index is the real guarantee.
- **Deleting a book force-closes its open loan** (the "lost book" flow) and reports it.
- **Deleting a user is refused (`409`) while they hold open loans** — their books are still out
  in the world. The asymmetry with book delete is deliberate.
- **Loans are append-only history.** A loan can only be closed (`POST /loans/{id}/return`), never
  edited or deleted.
- **Loan documents snapshot the book title/author and user name at borrow time.** Renaming a user
  or deleting a book never rewrites history.

Accepted limitations (documented rather than engineered away):

- Loans may be created with a backdated `borrowedAt`. Only "not in the future" and "book not
  currently on loan" are checked, so backdating can create historically overlapping loans.
- Without multi-document transactions (standalone Mongo), a narrow race exists between the
  open-loans check and a user delete. Insight pipelines drop dangling references, so it degrades
  gracefully.

---

## API quick reference

Interactive docs: the **Scalar** UI at `/scalar/v1` on the `api` resource (the root URL redirects
there).

```
# Books
GET    /books?limit=20
GET    /books/{id}
POST   /books                 { "title", "author", "pageCount", "year" }
DELETE /books/{id}            # force-closes any open loan (lost-book flow)

# Users
GET    /users?limit=20
GET    /users/{id}
POST   /users                 { "name" }
PUT    /users/{id}            { "name" }   # name is the only mutable field
DELETE /users/{id}            # 409 while the user holds open loans

# Loans
GET    /loans?limit=20&userId=&bookId=&openOnly=true
GET    /loans/{id}
POST   /loans                 { "bookId", "userId", "borrowedAt"? }   # default: now
POST   /loans/{id}/return     { "returnedAt"? }                       # default: now

# Insights
GET /insights/most-borrowed?limit=10&from=2026-01-01&to=2026-12-31
GET /insights/top-borrowers?limit=10&from=2026-01-01&to=2026-07-01
GET /insights/reading-pace?userId={userId}&bookId={bookId}
GET /insights/co-borrowed/{bookId}?limit=10
```

Conventions:

- **Cursor pagination** on every list endpoint: `?limit=&cursor=` returns
  `{ "items": [...], "nextCursor": "..." }`; `nextCursor` is `null` on the last page. Cursors are
  **opaque** — pass them back exactly as received. A cursor from one endpoint is rejected by
  another with `400`.
- **Limits**: `limit` must be positive (`400` otherwise) and is capped at **1000**; larger values
  are silently clamped.
- **Timestamps are UTC.** Send dates as plain ISO-8601 without a timezone offset
  (`2026-01-01T00:00:00`); values with offsets are not interpreted. Windows are half-open
  `[from, to)`.

---

## Running the application

### Prerequisites

- **.NET 10 SDK** (`10.0.201`+)
- **Docker** — required for the Mongo container and for every test tier except unit tests.
  Podman works too (below).
- **A trusted HTTPS dev certificate** — Aspire needs it to start (below).

### Start everything

```bash
dotnet run --project BookLibrary.AppHost
```

The Aspire dashboard opens automatically with logs, traces and metrics for every resource. Open
the `api` resource and go to `/scalar/v1` for the interactive API reference.

On first run the seeder loads deterministic sample data from embedded JSON
(`BookLibrary.Seeder/Data/`): 100+ real-world books, 15+ users and a few hundred loans, shaped to
exercise every insight and every reading-pace branch. It skips seeding if data already exists.

### Using Podman instead of Docker

Tell Aspire to use Podman and make sure the Podman machine is running:

```bash
# macOS/Linux
export ASPIRE_CONTAINER_RUNTIME=podman

# Windows (PowerShell), persists for future sessions
[System.Environment]::SetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME", "podman", "User")

podman machine start
```

### Trusting the HTTPS dev certificate

If the AppHost fails during startup or resources fail health checks, the local dev certificate is
probably not trusted:

```bash
dotnet dev-certs https --trust
dotnet dev-certs https --check --trust   # verify
```

- **Windows/macOS**: the command installs the cert into the OS trust store; confirm the one-time
  prompt.
- **Linux**: `--trust` alone is not enough — export the cert and add it to your distro's store:
  ```bash
  dotnet dev-certs https -ep ~/aspnet-dev-cert.crt --format PEM
  sudo cp ~/aspnet-dev-cert.crt /usr/local/share/ca-certificates/
  sudo update-ca-certificates
  ```
- **WSL2**: generate and trust the cert *inside* WSL2 — it has its own cert store, separate from
  Windows.

---

## Tests

All tests use **TUnit** and run through `dotnet test` (Microsoft.Testing.Platform).

```bash
dotnet test                                                  # everything (needs Docker)
dotnet test --project BookLibrary.Catalog.Tests              # unit + integration + functional
dotnet test --project BookLibrary.Api.Tests                  # REST-edge unit tests
dotnet test --project BookLibrary.SystemTests                # full-system flows
```

| Tier | Where | Needs Docker | Covers |
| --- | --- | :---: | --- |
| **Unit** | `Catalog.Tests/Unit`, `Api.Tests` | no | Reading-pace branches, counted-borrow rule, mapping, cursor encoding, cache keys. |
| **Integration** | `Catalog.Tests/Integration` | yes | Repositories and aggregation pipelines against real Mongo (Testcontainers). |
| **Functional** | `Catalog.Tests/Functional` | yes | The gRPC service surface — validation, status codes, mapping — over an in-memory host + Mongo. |
| **System** | `SystemTests` | yes | Complete HTTP → gRPC → Mongo flows via `Aspire.Hosting.Testing`. |

Only the Docker-free unit tier:

```bash
dotnet test --project BookLibrary.Catalog.Tests --treenode-filter "/*/*.Unit/*/*"
```

> **Docker is a hard prerequisite** for the integration, functional and system tiers. With Docker
> stopped, only unit tests (and the warm-up tests) run.

### Warm-up exercises

```bash
dotnet test --project BookLibrary.WarmUp.Tests
```

---

## How this scales

The current design is deliberately the simplest correct one for the assignment's scale. This is
the path when real load arrives — in order, driven by measurement, not upfront:

1. **Scale out the stateless tiers.** Api and Catalog hold no state; run replicas behind a load
   balancer. Aspire service discovery already abstracts the endpoints. Keyset (cursor) pagination
   is already replica- and offset-safe, unlike skip/limit.
2. **Add a distributed cache tier.** `HybridCache` takes Redis as an L2 by registration only — no
   call-site changes — which also makes insight cache invalidation work across Api replicas.
   Today, with in-memory caching, each replica evicts only its own copy and the TTLs bound the
   staleness.
3. **Make the counted-borrow filter indexable.** The rule is currently evaluated per query with a
   computed expression, which no index can serve. Persisting a precomputed flag or duration on
   loan close turns every insight filter into a plain indexed match. Cheap, and likely the first
   thing measurement would point at.
4. **Scale the database.** Replica set; route insight reads to secondaries; switch ids to binary
   GUIDs; shard `Loans` by book or time if a single node is ever outgrown.
5. **Move insights to background aggregation.** When on-demand pipelines get too expensive, a
   separate aggregator service consumes loan changes and maintains materialized insight
   collections — **bucketed counters** (daily counted-borrow counts per book and per user), so
   arbitrary `[from, to)` windows are answered by summing buckets with an index. The read side
   swaps behind the same gRPC contract; nothing above Catalog changes. Design notes:
   - **Getting changes to the aggregator — two sound options.**
     **CDC (Mongo change streams):** the database itself is the event source, so every committed
     write is captured — nothing can be missed — at the cost of consumers being coupled to the
     document schema and of running a replica set (needed for change streams anyway).
     **Transactional outbox with domain events:** the write path stores an event
     (`LoanCreated`, `LoanReturned`, ...) in an outbox collection in the same transaction as the
     domain write, and a relay publishes it to a service bus. Consumers get clean, semantic,
     schema-independent events with bus-grade retries, retention and dead-lettering.
     What is **not** an option: publishing to a bus directly from the write path without an
     outbox. Writing the database and publishing the message are two non-atomic operations, and a
     crash between them silently loses or fabricates an event (the dual-write problem).
   - **Delivery is at-least-once either way**, so aggregate updates must be idempotent, and
     events for the same book/loan must apply in order (partition by key on a bus).
   - **Materialized data is disposable.** The aggregator needs a rebuild path — recompute from
     `Loans`, then resume the stream — which doubles as reconciliation for counter drift.
   - **Not everything should be materialized.** Reading pace stays on-demand (a point query).
     Co-borrowed pairs grow quadratically per active user; a periodic batch recompute of top-N
     per book beats streaming updates there.
   - **Insights become eventually consistent** — fine for a librarian dashboard, but it's a
     product decision and should be stated as one.
