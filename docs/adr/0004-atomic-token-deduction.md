# ADR-0004 — Atomic token deduction with a conditional UPDATE

**Status:** Accepted

## Context

Two operations spend tokens: boosting a listing, and accepting a discount offer. Both originally followed the obvious pattern:

```csharp
var user = await db.Users.FindAsync(userId);
if (user.TokenBalance < amount) return Error("Insufficient tokens");
user.TokenBalance -= amount;
await db.SaveChangesAsync();
```

Between the read and the write there is a window in which a second request reads the same balance. Both pass the check, both subtract, and the balance goes negative.

This was not a theoretical concern. `TokenConcurrencyTests` fires eight parallel boost requests against a balance sufficient for exactly one. **All eight succeeded.**

## Decision

Make the check and the subtraction a single SQL statement, and wrap the surrounding work in a transaction.

```csharp
var affected = await db.Users
    .Where(u => u.Id == userId && u.TokenBalance >= amount)
    .ExecuteUpdateAsync(s => s.SetProperty(
        u => u.TokenBalance,
        u => u.TokenBalance - amount));

if (affected == 0) { /* insufficient funds */ }
```

Which produces:

```sql
UPDATE AspNetUsers
   SET TokenBalance = TokenBalance - @amount
 WHERE Id = @id AND TokenBalance >= @amount
```

## Consequences

**What it buys.**

The database holds an exclusive lock on the row for the duration of the statement. The second request waits, then re-evaluates the condition against the already-reduced balance. Zero affected rows means insufficient funds — the condition and the mutation cannot be separated by anything.

No application-level locking, no optimistic concurrency token, no retry loop.

**What it costs.**

Three things had to be handled explicitly, and each is a place where the pattern is easy to get wrong.

**The change tracker is bypassed.** `ExecuteUpdateAsync` issues SQL directly, so any tracked `ApplicationUser` instance is now stale. The balance is re-read from the database before writing the ledger row, so `BalanceAfter` is accurate. Forgetting this produces a ledger that silently disagrees with the balance.

**A transaction is required around the whole operation.** A crash between debiting tokens and inserting the `ListingBoost` row would leave the user with neither tokens nor boost. Both spending paths open an explicit transaction and roll back on failure.

**The error message needs a second query.** Zero affected rows tells you the condition failed but not what the balance actually is, so producing a useful message ("your balance is 1.5, you need 3") costs one more read — on the failure path only.

The pattern also requires EF Core 7 or later, and does not work against EF InMemory, which is one of the reasons the test suite runs on a real SQL Server ([ADR-0001](0001-no-repository-layer.md)).

## Alternatives considered

**Optimistic concurrency with a `rowversion` column.** Rejected: it converts the race into a `DbUpdateConcurrencyException` that the caller must catch and retry. That is more code, and under contention it retries repeatedly where the conditional update simply queues on the lock.

**`SELECT … FOR UPDATE` / `UPDLOCK` hint, then update.** Rejected: it achieves the same thing in two round trips instead of one, and requires raw SQL or an interceptor to express in EF Core.

**A distributed lock in Redis.** Rejected, and this one is worth stating clearly: it would make balance correctness depend on Redis being available. That directly contradicts [ADR-0005](0005-redis-fail-open.md) — Redis is an accelerator, never a correctness requirement. Money-like state belongs under the database's own guarantees.

**Serializable transaction isolation.** Rejected: it solves the problem with far more locking than necessary, and invites deadlocks between unrelated operations.
