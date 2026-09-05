# ADR-0001 — No repository layer over EF Core

**Status:** Accepted

## Context

The conventional layering for an ASP.NET Core API puts a repository interface between the service layer and EF Core: `IListingRepository`, `IBookingRepository`, and so on, usually with a `IUnitOfWork` alongside. The stated benefits are testability through mocking, and the ability to swap the data access technology.

## Decision

Services take `AppDbContext` directly. There are no repository interfaces and no unit-of-work abstraction. Services are `sealed` and receive their dependencies through primary constructors.

## Consequences

**What it buys.**

`DbSet<T>` already is a repository and `AppDbContext` already is a unit of work; a hand-written layer over them mostly forwards calls. Removing it keeps capabilities that a repository interface tends to hide:

- `IQueryable` composition, which the four-tier search depends on — each tier refines the previous tier's query rather than materializing it
- `Include` chains expressed at the call site, where the caller knows what it needs
- `ExecuteUpdateAsync`, used in twelve places, including the atomic token deduction in [ADR-0004](0004-atomic-token-deduction.md)
- projection straight into DTOs, so a listing query does not materialize entities it will throw away

**What it costs.**

Services cannot be mocked. `sealed` prevents inheritance, and there is no interface to substitute. Every business rule must therefore be verified against a real database.

That cost is paid deliberately, and it turned out to be worth paying: mocking a `DbContext` means mocking a LINQ provider, so the test runs against `List<T>` in memory. It would pass while production fails on the actual SQL. The rules in this system — a unique constraint that prevents a double referral, a row lock that prevents a negative balance, a `LIKE` that has to match folded text — do not exist in C# at all. They exist in the interaction between code and database, which is the only place they can be tested.

The integration suite runs against a real SQL Server through Testcontainers. It is slower (10–25 seconds warm) than a mocked unit suite would be, and it requires Docker to be running locally. See [testing.md](../testing.md).

## Alternatives considered

**Repository interfaces with mocked unit tests.** Rejected: the tests would verify the mock's behaviour rather than the database's. `TokenConcurrencyTests` is the concrete proof — it depends on the database holding an exclusive row lock, and no mock reproduces that.

**Repository interfaces with integration tests anyway.** Rejected: this pays the cost of the abstraction without collecting its only real benefit. If the tests hit a real database regardless, the interface is pure indirection.

**Generic `IRepository<T>`.** Rejected: it inevitably grows an `IQueryable Query()` escape hatch, at which point it is `DbSet<T>` with extra steps.
