# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

BookLibrary is a .NET 10 solution with two parts:

- **The library API (main task)** — a layered service that answers a librarian's operational questions (most-borrowed books, top borrowers, reading pace, co-borrowed titles) over a **mandatory gRPC** boundary with **MongoDB** storage, orchestrated by **.NET Aspire**. See `README.md` for architecture, business rules and run/test instructions, and `docs/data-schema.md` for the persistence model.
- **The warm-up exercises** — `BookLibrary.WarmUp` (small algorithmic exercises in `Tasks.cs`) and `BookLibrary.WarmUp.Tests`, independent of the main task.

### Projects

| Project | Role |
| --- | --- |
| `BookLibrary.Api` | REST edge (minimal APIs + Scalar). HTTP concerns only; a gRPC *client* of Catalog. |
| `BookLibrary.Catalog` | gRPC *server*. All business logic, insight aggregations and Mongo access (`Domain/`, `Data/`, `Insights/`, `Mapping/`, `Services/`). |
| `BookLibrary.Contracts` | The `.proto` contract; generates the gRPC server base + typed client. |
| `BookLibrary.Seeder` | One-shot, idempotent sample-data loader. |
| `BookLibrary.AppHost` | Aspire orchestration: Mongo container + Catalog, Seeder, Api. |
| `BookLibrary.ServiceDefaults` | Shared OpenTelemetry, health checks, resilience, service discovery. |
| `BookLibrary.WarmUp` (+ `.Tests`) | Warm-up exercises, separate from the main task. |

The REST edge reaches Catalog over **h2c (`http://catalog`)** — a plain scheme `GrpcChannel` understands, rewritten to the real endpoint by Aspire's service-discovery handler (not the `https+http` discovery scheme, which the channel can't resolve).

## Commands

Run from the repository root. The solution file is `BookLibrary.slnx` (the newer XML solution format; `dotnet` picks it up automatically).

```bash
dotnet build                      # build the solution
dotnet restore                    # restore NuGet packages
dotnet format                     # apply code style / formatting
```

### Running the app

```bash
dotnet run --project BookLibrary.AppHost   # starts Mongo + Catalog + Seeder + Api via Aspire
```

The Aspire dashboard opens automatically; the Scalar API reference is the `api` resource's `/scalar/v1` (the API root redirects there). **Docker is required** — the AppHost runs MongoDB as a container. The seeder loads deterministic sample data on first run and skips if already populated.

### Tests

Tests use **TUnit**, which runs on Microsoft.Testing.Platform (MTP). The root `global.json` opts `dotnet test` into MTP mode (`"test": { "runner": "Microsoft.Testing.Platform" }`), so the standard command works solution-wide — no legacy VSTest bridge props needed:

```bash
dotnet test                                                       # run all tests
dotnet test --project BookLibrary.WarmUp.Tests                    # run one test project
dotnet test --treenode-filter "/*/*/TasksTests/*"                # filter by class
dotnet test --treenode-filter "/*/*/*/IsPowerOfTwo_*"            # filter by test name
```

The main task's tests span four tiers. **Docker is a hard prerequisite for every tier except unit** (integration/functional use Testcontainers-Mongo; system uses `Aspire.Hosting.Testing`):

| Tier | Where | Needs Docker | Covers |
| --- | --- | :---: | --- |
| Unit | `BookLibrary.Catalog.Tests/Unit` | no | Reading-pace branches, counted-borrow rule, domain→contract mapping. |
| Integration | `BookLibrary.Catalog.Tests/Integration` | yes | Aggregation pipelines against real Mongo. |
| Functional | `BookLibrary.Catalog.Tests/Functional` | yes | gRPC service surface (validation, status codes, mapping) over an in-memory host + Mongo. |
| System | `BookLibrary.SystemTests` | yes | Full HTTP→gRPC→Mongo flows via Aspire.Hosting.Testing. |

```bash
dotnet test --project BookLibrary.Catalog.Tests                       # unit + integration + functional
dotnet test --project BookLibrary.SystemTests                         # full-system flows
dotnet test --project BookLibrary.Catalog.Tests --treenode-filter "/*/*.Unit/*/*"   # Docker-free unit tier only
```

Note the MTP-mode CLI shape differs from legacy VSTest: pass a project with `--project` (not positionally), a solution with `--solution`, and MTP arguments directly (no extra `--`). Running the test project as an executable (`dotnet run --project BookLibrary.WarmUp.Tests`) also works.

TUnit specifics: tests are `[Test]` methods that return `Task`; assertions are awaited (`await Assert.That(x).IsEqualTo(y)`); parameterize with `[Arguments(...)]`. Because `Span<char>`/`ReadOnlySpan<char>` are ref structs that can't cross an `await`, materialize span/enumerable results to a `string` or array *before* asserting.

Test naming convention (required): `Method_WhenWhat_ShouldWhat`.

## Conventions

- Targets `net10.0` with `ImplicitUsings` and `Nullable` both enabled — omit redundant `using`s and treat nullability warnings as real.
- Code is written with performance in mind (e.g. `ArrayPool<char>`, `Span<char>`, bit-twiddling). Existing comments in `Tasks.cs` deliberately note where a simpler BCL-based approach (`BitOperations.IsPow2`, `Enumerable.Reverse`) would be preferred for real-world code — these are intentional exercises, not oversights.
- `[PublicAPI]` (from JetBrains.Annotations) marks types whose members are used reflectively/externally so the IDE does not flag them as unused.
- The project is developed in JetBrains Rider (`.idea/`, `riderModule.iml`, `_ReSharper.Caches/` are git-ignored).

Main-task idioms:

- **Domain == persistence model.** `BookLibrary.Catalog/Domain/` types carry the BSON attributes directly; the gRPC contract is the only separate model, mapped with **Mapperly** (compile-time, no reflection — no AutoMapper).
- **The gRPC contract is the seam.** Change `Contracts/Protos/catalog.proto`, then rebuild so the generated server base/client update before touching `CatalogGrpcService` or the REST edge.
- **Ids are `Guid` stored as strings** (`[BsonRepresentation(BsonType.String)]`); timestamps are **UTC**; time windows are half-open `[from, to)`.
- **Insights are computed on demand** via Mongo aggregation pipelines (`Data/InsightRepository.cs`) — no background worker or materialized views. The **counted-borrow** rule is applied at query time, never persisted.
- **Structured logging via `[LoggerMessage]`** source generators (the containing class must be `partial`); use `ILogger<T>` via DI — no `Console.WriteLine` or static loggers.
- Backend gRPC status codes map to HTTP at the REST edge (`NotFound → 404`, `InvalidArgument → 400`) via `RpcExceptionHandler` → ProblemDetails.

## Documentation

- **`docs/data-schema.md` must stay in sync with how data is stored or accessed.** Whenever you change the persistence model or data access — domain/persistence classes (`BookLibrary.Catalog/Domain/`), collection wiring or BSON mapping (`BookLibrary.Catalog/Data/LibraryDb.cs`), indexes (`MongoIndexInitializer.cs`), the aggregation/query pipelines (`InsightRepository.cs`, repositories), or the seeded sample shape (`BookLibrary.Seeder/SampleData.cs`) — review `docs/data-schema.md` and update it if the change affects the documented collections, fields, relationships, indexes, or modelling decisions.
