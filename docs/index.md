# Documentation

Everything about how the Uslužionica backend works, why it works that way, and what it costs.

## Foundations

| | |
|---|---|
| [Architecture](architecture.md) | Layers, startup sequence, middleware pipeline, request lifecycle, and why there is no repository layer |
| [Domain model](domain-model.md) | 20 entities, their relationships, the constraints that encode business rules, and the indexes behind the hot queries |
| [API reference](api-reference.md) | All 77 endpoints grouped by module, with access levels, rate limits and response conventions |

## Subsystems

| | |
|---|---|
| [Search engine](search.md) | Four-tier search over Serbian text — diacritic folding, Cyrillic transliteration, pigeonhole prefiltering and OSA distance |
| [Token economy](token-economy.md) | How tokens are earned and spent, the anti-abuse rules, and the concurrency bug that shaped the implementation |
| [Real-time](realtime.md) | Two SignalR hubs, JWT over WebSockets, group design, presence tracking and message encryption at rest |
| [Scaling & caching](scaling.md) | The five jobs Redis does, and the rule that none of them may become a hard dependency |

## Operations

| | |
|---|---|
| [Security](security.md) | Auth flow, token lifetimes, rate limiting, secret handling, container hardening — and the known limitations |
| [Testing](testing.md) | Why business rules are integration-tested, what EF InMemory cannot prove, and how the coverage gate works |
| [Deployment](deployment.md) | The image, both Compose stacks, the Hetzner and Azure pipelines, and how to roll back |

## Decisions

Eight [architecture decision records](adr/README.md) covering the choices that shaped the codebase — each with the context that forced it, the alternatives rejected, and what it costs.

Several were made only after the naive version had already failed in a measurable way. [ADR-0004](adr/0004-atomic-token-deduction.md) exists because eight parallel requests all successfully spent the same tokens.

## Reading order

Coming to this cold, the shortest useful path is:

1. [Architecture](architecture.md) — how the pieces fit
2. [Domain model](domain-model.md) — what the pieces operate on
3. [Search engine](search.md) or [Token economy](token-economy.md) — whichever problem interests you more

Everything else is reference material, reachable from those three.
