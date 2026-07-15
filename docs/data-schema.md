# Data schema

The system stores everything in a single MongoDB database — **`library`** — across three
collections: **`Books`**, **`Users`** and **`Loans`**. This document describes how those documents
are shaped, how they relate, and the deliberate modelling choices behind them.

> The domain model *is* the persistence model (see [Design notes](#design-notes)). The C# types
> live in `BookLibrary.Catalog/Domain/`; collection names and the typed accessor live in
> `BookLibrary.Catalog/Data/LibraryDb.cs`.

---

## Entity relationships

```
   Users                         Loans                          Books
 ┌─────────┐   1        many  ┌──────────────┐  many        1  ┌────────────┐
 │ _id     │◀──────────────── │ UserId       │                 │ _id        │
 │ Name    │                  │ BookId       │ ───────────────▶│ Title      │
 └─────────┘                  │ BorrowedAt   │                 │ Author     │
                              │ ReturnedAt?  │                 │ PageCount  │
                              └──────────────┘                 └────────────┘
```

A **`Loan`** is the central fact: it records that one **`User`** borrowed one **`Book`** at a point
in time, and — if returned — when it came back. Every insight is derived by aggregating loans and
joining the referenced book or user.

References are by id only; there is no database-enforced referential integrity (see
[Design notes](#design-notes)).

---

## Collections

### `Books`

| Field       | BSON type | C# type  | Notes |
| ----------- | --------- | -------- | ----- |
| `_id`       | string    | `Guid`   | Primary key. Guid stored as its string form. |
| `Title`     | string    | `string` | Required. |
| `Author`    | string    | `string` | Required. |
| `PageCount` | int32     | `int`    | Total pages. May be `0` (unknown), which makes reading pace uncomputable for the book. |

```json
{
  "_id": "00000000-0000-0000-0001-000000000001",
  "Title": "Clean Code",
  "Author": "Robert C. Martin",
  "PageCount": 464
}
```

### `Users`

| Field   | BSON type | C# type  | Notes |
| ------- | --------- | -------- | ----- |
| `_id`   | string    | `Guid`   | Primary key. Guid stored as its string form. |
| `Name`  | string    | `string` | Required. Authentication/authorization are out of scope, so a user is just an identity and a display name. |

```json
{
  "_id": "00000000-0000-0000-0002-000000000001",
  "Name": "Alice"
}
```

### `Loans`

| Field        | BSON type      | C# type     | Notes |
| ------------ | -------------- | ----------- | ----- |
| `_id`        | string         | `Guid`      | Primary key. |
| `BookId`     | string         | `Guid`      | The borrowed book's `_id`. |
| `UserId`     | string         | `Guid`      | The borrowing user's `_id`. |
| `BorrowedAt` | date (UTC)     | `DateTime`  | When the book left the shelf. |
| `ReturnedAt` | date (UTC) \| null | `DateTime?` | When it came back. `null` = **open loan** (still out). |

```json
{
  "_id": "00000000-0000-0000-0003-000000000001",
  "BookId": "00000000-0000-0000-0001-000000000001",
  "UserId": "00000000-0000-0000-0002-000000000001",
  "BorrowedAt": { "$date": "2026-06-05T00:00:00Z" },
  "ReturnedAt": { "$date": "2026-06-15T00:00:00Z" }
}
```

An **open loan** omits the return date:

```json
{
  "_id": "00000000-0000-0000-0003-000000000003",
  "BookId": "00000000-0000-0000-0001-000000000003",
  "UserId": "00000000-0000-0000-0002-000000000001",
  "BorrowedAt": { "$date": "2026-07-03T00:00:00Z" },
  "ReturnedAt": null
}
```

---

## Indexes

Loans carry the analytical load, so they are indexed on the fields the insight pipelines group and
filter by. Indexes are (idempotently) created on startup by
`BookLibrary.Catalog/Data/MongoIndexInitializer.cs`.

| Collection | Index | Serves |
| ---------- | ----- | ------ |
| `Loans` | `{ BookId: 1 }` | Most-borrowed grouping, co-borrowed lookups. |
| `Loans` | `{ UserId: 1 }` | Top-borrowers grouping. |
| `Loans` | `{ BorrowedAt: 1 }` | Window (`[from, to)`) filtering. |
| `Loans` | `{ UserId: 1, BookId: 1 }` | Reading-pace lookup (a user's loans of one book). |

`Books` and `Users` rely on the default `_id` index for their point lookups and for the insight
`$lookup` joins.

---

## Design notes

- **Guids stored as strings.** Every `_id` and foreign-key field is a `Guid` persisted via
  `[BsonRepresentation(BsonType.String)]`, so documents stay human-readable during review. At high
  write volume a 16-byte binary representation would be preferred; the perf delta is irrelevant at
  this scale. Ids are generated with `Guid.CreateVersion7()` (time-ordered).
- **Domain == persistence model.** The gRPC contract is the only separate model. A distinct
  persistence DTO layer would add ceremony without value here, so the domain classes carry the BSON
  mapping attributes directly.
- **No enforced referential integrity.** `Loan.BookId` / `Loan.UserId` are plain references; MongoDB
  does not enforce them. The insight pipelines join with `$lookup` and simply drop loans whose
  referenced book or user no longer exists (via `$unwind`), so a dangling reference degrades
  gracefully rather than erroring.
- **UTC everywhere.** Timestamps are stored and compared in UTC. Time windows are half-open
  `[from, to)` to make them composable without double-counting boundaries.
- **The "counted borrow" rule is applied at query time, not stored.** Whether a loan counts toward an
  insight (open, or held at least one day) is computed by the aggregation pipelines and the
  reading-pace logic — it is not a persisted flag — so the rule has a single definition and the raw
  loan history stays intact. See the business rules in the [README](../README.md#business-rules-what-the-numbers-mean).

---

## Sample data

`BookLibrary.Seeder` populates deterministic, fixed-id documents on first run (covering every insight
and every reading-pace branch). Ids follow a readable convention — books `…-0001-…`, users
`…-0002-…`, loans `…-0003-…` — so seeded documents are easy to spot and reference. See
`BookLibrary.Seeder/SampleData.cs`.
