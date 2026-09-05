# ADR-0005 — Redis fails open, always

**Status:** Accepted

## Context

Redis was introduced to do five things: cache objects, act as the SignalR backplane, hold Data Protection keys, provide distributed locks for background jobs, and track presence.

Adding it created a question that had to be answered once, globally, rather than case by case: **is Redis a dependency or an accelerator?**

The environments where it would be absent are not exotic. Local development runs without it. Integration tests run without it in CI. And in production it can simply fall over.

## Decision

> Redis is an accelerator and a place for shared state. It is never a condition for the application to run.

The application must start and serve traffic when Redis is not configured at all, and when it is configured but dead. No user request may fail because of a cache.

Concretely:

- `AbortOnConnectFail = false` on every connection
- `RedisConnection.IsAvailable` so callers ask rather than catch
- `CacheService` swallows every error and reports a miss
- Every Redis-backed component has a working non-Redis path
- The `/health` endpoint excludes Redis; only `/health/full` includes it

## Consequences

**What it buys.**

`docker compose up` works without a cache container. The integration suite runs in CI without Redis. Losing Redis in production makes the application slower, not broken.

The graceful paths are specific and each is correct on its own:

| Component | Without Redis |
|---|---|
| `CacheService` | every read misses, so every read goes to SQL Server |
| `CacheInvalidator` | no pub/sub message; each instance falls back on its TTL, so staleness is bounded at 10 minutes rather than milliseconds |
| `OnlineTracker` | `ConcurrentDictionary` in process memory — correct for a single instance |
| `DistributedLock` | acquisition is treated as successful — correct for a single instance |
| SignalR backplane | not registered; single-instance delivery is unaffected |
| Data Protection | falls back to the default provider |

**What it costs.**

Every degraded path is only *correct* for a single instance. With two instances and Redis down, presence is wrong, background jobs run twice, and Data Protection keys diverge. The system does not tell you this is happening — it just behaves oddly.

`/health/full` exists precisely so that state is visible without being treated as a reason to pull an instance out of rotation.

The `AbortOnConnectFail = false` behaviour also means a misconfigured Redis connection string does not fail loudly at startup. The application comes up and quietly operates degraded. That is the correct trade for an outage and the wrong one for a typo, and it is a deliberate acceptance of the latter.

Finally, the rule constrains future design: nothing that must be correct may be built on Redis. That is why token deduction uses a database row lock rather than a distributed lock ([ADR-0004](0004-atomic-token-deduction.md)).

## Alternatives considered

**Redis as a hard dependency.** Rejected. It would mean a cache container in every developer's local stack and in CI, and — far worse — that a Redis outage becomes a full site outage. A cache that can take the site down is not a cache.

**Fail-closed only in Production.** Rejected: two behaviours means the degraded path is never exercised where it matters, and would be discovered to be broken during the first real outage.

**Wrapping each call site in try/catch.** Rejected: the same defensive block repeated at dozens of call sites, one of which will eventually be forgotten. Centralising it in `CacheService` and `RedisConnection` means callers cannot get it wrong.

**`IDistributedCache`.** Rejected: it deals in byte arrays and has no notion of get-or-compute, so the serialization and error handling would be duplicated at every call site anyway.

## The incident that produced the health-check rule

With one health endpoint running all checks, stopping Redis returned 503 — while `/api/categories`, search and everything else kept working. Docker marked the container unhealthy and a load balancer would have removed it from rotation, because of a cache.

Tagging the Redis check `cache` does nothing by itself; the endpoint needs a predicate that actually filters on the tag:

```csharp
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => !check.Tags.Contains("cache")
});
```

That missing predicate is the whole difference between "Redis is down" and "the site is down".
