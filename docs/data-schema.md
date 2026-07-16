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
 │ Name    │                  │ UserName ▪   │                 │ Title      │
 └─────────┘                  │ BookId       │ ───────────────▶│ Author     │
                              │ BookTitle  ▪ │                 │ PageCount  │
                              │ BookAuthor ▪ │                 │ Year       │
                              │ BorrowedAt   │                 └────────────┘
                              │ ReturnedAt?  │
                              └──────────────┘   ▪ = snapshot copied at borrow time
```

A **`Loan`** is the central fact: it records that one **`User`** borrowed one **`Book`** at a point
in time, and — if returned — when it came back. Every insight is derived by aggregating loans and
joining the referenced book or user.

Each loan also **snapshots** the borrower's name and the book's title/author as they were at borrow
time (the `▪` fields above). These are historical facts about the event — like `BorrowedAt` — not
live pointers: they let loan history stay intact and readable after the referenced user or book is
deleted, and preserve the borrower's name as it was even if the user is later renamed. The `UserId`
/ `BookId` references are kept alongside as the identity and join key. See
[Design notes](#design-notes).

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
| `Year`      | int32     | `int`    | Year of publication. May be `0` (unknown). |

```json
{
  "_id": "00000000-0000-0000-0001-000000000001",
  "Title": "Clean Code",
  "Author": "Robert C. Martin",
  "PageCount": 464,
  "Year": 2008
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
| `BookTitle`  | string         | `string`    | **Snapshot** of the book's `Title` at borrow time (see [Design notes](#design-notes)). |
| `BookAuthor` | string         | `string`    | **Snapshot** of the book's `Author` at borrow time. |
| `UserId`     | string         | `Guid`      | The borrowing user's `_id`. |
| `UserName`   | string         | `string`    | **Snapshot** of the user's `Name` at borrow time. |
| `BorrowedAt` | date (UTC)     | `DateTime`  | When the book left the shelf. |
| `ReturnedAt` | date (UTC) \| null | `DateTime?` | When it came back. `null` = **open loan** (still out). |

```json
{
  "_id": "00000000-0000-0000-0003-000000000001",
  "BookId": "00000000-0000-0000-0001-000000000001",
  "BookTitle": "Clean Code",
  "BookAuthor": "Robert C. Martin",
  "UserId": "00000000-0000-0000-0002-000000000001",
  "UserName": "Alice",
  "BorrowedAt": { "$date": "2026-06-05T00:00:00Z" },
  "ReturnedAt": { "$date": "2026-06-15T00:00:00Z" }
}
```

An **open loan** omits the return date:

```json
{
  "_id": "00000000-0000-0000-0003-000000000003",
  "BookId": "00000000-0000-0000-0001-000000000003",
  "BookTitle": "Domain-Driven Design",
  "BookAuthor": "Eric Evans",
  "UserId": "00000000-0000-0000-0002-000000000001",
  "UserName": "Alice",
  "BorrowedAt": { "$date": "2026-07-03T00:00:00Z" },
  "ReturnedAt": null
}
```

---

## Indexes

Loans carry the analytical load, so they are indexed on the fields the insight pipelines group and
filter by. Indexes are (idempotently) created on startup by
`BookLibrary.Catalog/Data/MongoIndexInitializer.cs`.

| Collection | Index | Collation | Serves |
| ---------- | ----- | --------- | ------ |
| `Loans` | `{ BookId: 1 }` | default | Most-borrowed grouping, co-borrowed lookups. |
| `Loans` | `{ UserId: 1 }` | default | Top-borrowers grouping. |
| `Loans` | `{ UserId: 1, BookId: 1 }` | default | Reading-pace lookup (a user's loans of one book). |
| `Loans` | `{ BookId: 1 }` unique, partial (`ReturnedAt: null`) — named `ux_loans_open_book` | default | **Double-loan guard.** Enforces "a book cannot have two open loans" at the database level — see [Design notes](#design-notes). |
| `Loans` | `{ BorrowedAt: 1, _id: 1 }` | default | Cursor pagination over `/loans` (newest-first keyset); the `BorrowedAt` prefix also serves the window (`[from, to)`) filter. |
| `Books` | `{ Title: 1, _id: 1 }` — named `ix_books_title_id_ci` | `en`, strength 2 (case-insensitive) | Cursor pagination over `/books`, ordered case-insensitively. |
| `Users` | `{ Name: 1, _id: 1 }` — named `ix_users_name_id_ci` | `en`, strength 2 (case-insensitive) | Cursor pagination over `/users`, ordered case-insensitively. |

`Books` and `Users` no longer rely solely on the default `_id` index — each also has the compound
index above for cursor-paginated listing.

- **Book/User listings sort case-insensitively.** `/books` and `/users` order by `Title`/`Name`
  using a shared collation (`en`, strength 2 — case-insensitive, accent-sensitive), defined once as
  `BookLibrary.Catalog/Data/ListingCollation.cs` and applied to both the index (`CreateIndexOptions.Collation`)
  and the `Find` query (`FindOptions.Collation`) so they can't drift apart — a query collation that
  doesn't match the index collation makes Mongo ignore the index. Without this, "apple" sorted after
  "Zebra" under Mongo's default binary (code-point) comparison. Guid strings (the `_id` keyset
  tiebreaker) are lowercase hex, so strength-2 collation orders them identically to a binary
  comparison — the tiebreaker's total order still holds when the primary sort key compares
  case-insensitively equal (e.g. "apple" and "Apple").
- **Existing (pre-collation) `Books`/`Users` indexes are additive, not replaced.** Mongo only rejects
  an index *definition* change under the same name (`IndexOptionsConflict`); the collated indexes
  above use new explicit names (`ix_books_title_id_ci` / `ix_users_name_id_ci`) distinct from the
  driver's auto-generated default names of the old indexes, so `MongoIndexInitializer` (create-only,
  runs on every boot) adds them alongside the old uncollated `(Title, _id)` / `(Name, _id)` indexes
  on a database seeded before this change, rather than crashing. The old indexes become redundant
  dead weight in that case (Mongo will use whichever index matches the query's collation, i.e. the
  new one, for listing) — drop them by hand on any long-lived database if reclaiming the space
  matters; a fresh database never creates them in the first place.

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
- **Loans snapshot the book/user display fields.** Alongside the `BookId`/`UserId` references, a loan
  stores `BookTitle`, `BookAuthor` and `UserName` as they were **at borrow time**. This is
  *snapshotting a historical fact* (the same category as `BorrowedAt`), not a denormalized cache:
  because a loan is an immutable record of a past event, there is nothing to keep in sync. It buys
  two things the spec calls for — loans are retained and stay readable after their book or user is
  **deleted**, and a later user **rename** does not rewrite the borrower's name in past loans. The
  ids remain the identity and the join key; the snapshots are the historical display. Note this is a
  deliberate, safe use of duplication limited to values that are either immutable (book fields) or
  whose historical value is the one we want (user name) — general reference data with its own
  lifecycle is still modelled by id, not copied.
- **Snapshots do not (yet) change insight semantics.** The insight pipelines still `$lookup` the live
  `Books`/`Users` collections rather than reading the snapshot fields, so a deleted book or user is
  still dropped from most-borrowed / top-borrowers (the pre-snapshot behaviour). Switching insights to
  count deleted entities via their snapshot is a deliberate future decision, not implied by storing
  the snapshot.
- **UTC everywhere.** Timestamps are stored and compared in UTC. Time windows are half-open
  `[from, to)` to make them composable without double-counting boundaries.
- **The "counted borrow" rule is applied at query time, not stored.** Whether a loan counts toward an
  insight (open, or held at least one day) is computed by the aggregation pipelines and the
  reading-pace logic — it is not a persisted flag — so the rule has a single definition and the raw
  loan history stays intact. See the business rules in the [README](../README.md#business-rules-what-the-numbers-mean).
- **One `Book` document = one physical copy.** There is no `copies` field or per-copy entity; two
  physical copies of the same title are two separate `Book` documents with distinct ids (duplicate
  titles are allowed for exactly this reason). This is what makes "a book cannot be borrowed while
  already on loan" coherent as a per-document rule.
- **The open-loan uniqueness invariant is enforced by index, not application code.** The unique
  partial index `ux_loans_open_book` (`{BookId: 1}` where `ReturnedAt: null`) is what actually
  guarantees a book can't have two open loans under concurrent writes; the service layer's read
  check before insert only exists to produce a friendlier error message in the common case.
- **Cursor pagination requires a `(sortKey, _id)` total order.** `Books`/`Users`/`Loans` listings
  page by an opaque keyset cursor over `(Title, _id)`, `(Name, _id)` and `(BorrowedAt, _id)`
  respectively — the `_id` tiebreaker is required because the primary sort key (title, name,
  borrow timestamp) is not unique on its own, and a keyset cursor needs a total order to avoid
  skipping or repeating rows when values tie. This is why the compound indexes above exist.

---

## Sample data

`BookLibrary.Seeder` populates deterministic, fixed-id documents on first run (skipping if any book
already exists). The data lives in **embedded JSON files** under `BookLibrary.Seeder/Data/`
(`books.json`, `users.json`, `loans.json`); `BookLibrary.Seeder/SampleData.cs` deserializes them into
the domain types and inserts them. The catalogue is a curated real-world set — **100+ books**
(each with a `Year`), **15+ users**, and a few hundred loans.

- **Ids** follow a readable convention — books `…-0001-…`, users `…-0002-…`, loans `…-0003-…` — so
  seeded documents are easy to spot and reference.
- **Loan dates are relative.** `loans.json` stores each loan as an offset (`daysAgo` borrowed +
  `held` duration: `null` = still open, `0` = same-day/excluded, `>0` = days held) rather than an
  absolute timestamp. `BuildLoans(now)` resolves them against the seed-run time, so the year-to-date
  default window is always populated, and copies each loan's book/user snapshot from the seeded
  books/users so it always matches the catalogue.
- **The dataset is hand-shaped for stable insight answers** used by the system tests: "Clean Code"
  is the most-borrowed book, "Alice" the top borrower, Alice has exactly one 10-day / 464-page Clean
  Code loan (reading pace 46.4), and the curated books at the front (Clean Code, Pragmatic, DDD,
  Refactoring, Mythical Man-Month, and a zero-page zine) exercise every insight and reading-pace
  branch.
- **No book has two open loans.** `ux_loans_open_book` (see [Indexes](#indexes)) enforces this at the
  database level, so the seed data must satisfy it or the Catalog host fails to start. One seeded
  loan (`…-0001-000000000045`, borrowed by user `…-0002-000000000003`) was originally a second open
  loan on a book that already had one open loan; its `held` was changed from `null` to `120` (a
  completed 120-day loan) to satisfy the invariant. This is safe for the pinned insight answers above
  (the loan was already counted as an open loan by the counted-borrow rule; it stays counted) — **17**
  loans are open in the seeded data, not 18. Anyone adding seed loans must preserve the
  no-two-open-loans-per-book invariant or the seeder/Catalog boot will fail on the unique index.
