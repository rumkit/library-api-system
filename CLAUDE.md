# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

BookLibrary is an early-stage .NET 10 solution. It contains `BookLibrary.WarmUp` (small algorithmic warm-up exercises in `Tasks.cs`) and `BookLibrary.WarmUp.Tests` (TUnit unit tests). There is no application entry point or runtime host yet.

## Commands

Run from the repository root. The solution file is `BookLibrary.slnx` (the newer XML solution format; `dotnet` picks it up automatically).

```bash
dotnet build                      # build the solution
dotnet restore                    # restore NuGet packages
dotnet format                     # apply code style / formatting
```

### Tests

Tests use **TUnit**, which runs on Microsoft.Testing.Platform (MTP). The root `global.json` opts `dotnet test` into MTP mode (`"test": { "runner": "Microsoft.Testing.Platform" }`), so the standard command works solution-wide — no legacy VSTest bridge props needed:

```bash
dotnet test                                                       # run all tests
dotnet test --project BookLibrary.WarmUp.Tests                    # run one test project
dotnet test --treenode-filter "/*/*/TasksTests/*"                # filter by class
dotnet test --treenode-filter "/*/*/*/IsPowerOfTwo_*"            # filter by test name
```

Note the MTP-mode CLI shape differs from legacy VSTest: pass a project with `--project` (not positionally), a solution with `--solution`, and MTP arguments directly (no extra `--`). Running the test project as an executable (`dotnet run --project BookLibrary.WarmUp.Tests`) also works.

TUnit specifics: tests are `[Test]` methods that return `Task`; assertions are awaited (`await Assert.That(x).IsEqualTo(y)`); parameterize with `[Arguments(...)]`. Because `Span<char>`/`ReadOnlySpan<char>` are ref structs that can't cross an `await`, materialize span/enumerable results to a `string` or array *before* asserting.

Test naming convention (required): `Method_WhenWhat_ShouldWhat`.

## Conventions

- Targets `net10.0` with `ImplicitUsings` and `Nullable` both enabled — omit redundant `using`s and treat nullability warnings as real.
- Code is written with performance in mind (e.g. `ArrayPool<char>`, `Span<char>`, bit-twiddling). Existing comments in `Tasks.cs` deliberately note where a simpler BCL-based approach (`BitOperations.IsPow2`, `Enumerable.Reverse`) would be preferred for real-world code — these are intentional exercises, not oversights.
- `[PublicAPI]` (from JetBrains.Annotations) marks types whose members are used reflectively/externally so the IDE does not flag them as unused.
- The project is developed in JetBrains Rider (`.idea/`, `riderModule.iml`, `_ReSharper.Caches/` are git-ignored).
