# Architecture decision records

Eight decisions that shaped this codebase, each with the context that forced it, the alternatives considered, and what it costs.

These are written after the fact, reconstructed from the development log and the code. Every one of them was a real choice with a real trade-off, and several were made only after the naive version had already failed in a measurable way.

| # | Decision | Status |
|---|---|---|
| [0001](0001-no-repository-layer.md) | No repository layer over EF Core | Accepted |
| [0002](0002-signalr-for-realtime.md) | SignalR for chat and notifications | Accepted |
| [0003](0003-denormalized-search-columns.md) | Denormalized search columns instead of collation or full-text | Accepted |
| [0004](0004-atomic-token-deduction.md) | Atomic token deduction with a conditional UPDATE | Accepted |
| [0005](0005-redis-fail-open.md) | Redis fails open, always | Accepted |
| [0006](0006-relative-media-urls.md) | Media stored as relative paths | Accepted |
| [0007](0007-two-instalment-referral.md) | Referral reward paid in two instalments | Accepted |
| [0008](0008-hetzner-over-azure.md) | Hetzner for production, Azure maintained alongside | Accepted |

## Format

Each record has four sections:

- **Context** — the situation that made a decision necessary
- **Decision** — what was chosen
- **Consequences** — what it buys and what it costs, both stated
- **Alternatives considered** — what was rejected and why

A record whose consequences section lists no costs is not describing a decision; it is describing a preference.
