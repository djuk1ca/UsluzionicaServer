# ADR-0003 — Denormalized search columns instead of collation or full-text

**Status:** Accepted

## Context

Serbian text search has three problems at once:

1. **Two alphabets.** `фризер` and `frizer` are the same word.
2. **Diacritics people omit.** Someone searching for a haircut types `sisanje`, not `Šišanje`.
3. **`đ` transliterates to two characters.** `Đorđe` and `djordje` must match.

On top of that, compound words are long enough that typos are routine — `vodoinstalter` for `vodoinstalater`.

The requirement is that a query and the stored content end up in exactly the same form, so that matching is deterministic and testable.

## Decision

Store folded copies of the searchable text in dedicated columns, produced by one C# function:

| Column | Source |
|---|---|
| `Listing.SearchTitle` | `Title` |
| `Listing.SearchLocation` | `Location` |
| `Listing.SearchBody` | `Description` |
| `ApplicationUser.SearchName` | `FullName` |

`SearchNormalizer.Fold` is the single source of truth for matching, applied both when writing to the database and when parsing a query. A `SearchVersion` column records which ruleset version indexed each row.

The columns are populated from `AppDbContext.SaveChanges` via `SearchIndexer`, **not** from the services.

## Consequences

**What it buys.**

Matching behaviour lives in code, in one function, covered by 34 unit tests with no I/O. It is identical on a developer machine, in CI and in production, and it does not depend on any database setting.

`SearchLocation` being separate makes city filtering an index seek rather than a scan. `SearchBody` being separate is what allows the tiered search to skip the expensive `varchar(max)` scan until it is actually needed.

Maintaining the index from the change tracker makes it impossible to bypass. Had it lived in `ListingService.CreateAsync` and `UpdateAsync`, every future write path — an admin edit, a seed, a data fix — would be one forgotten line away from producing a listing that exists but cannot be found, with nothing to indicate why.

**What it costs.**

Storage roughly doubles for the indexed text, and every write does the folding work.

Changing the folding rules invalidates every existing row. That is why `SearchVersion` exists: bump `SearchNormalizer.Version`, and `SearchIndexBackfill` re-indexes every mismatched row on the next startup, in batches of 500 to avoid locking large ranges on a production database.

The backfill cannot be an EF migration, because SQL Server cannot execute a C# function. Writing `Fold()` as T-SQL would mean roughly forty nested `REPLACE` calls, collation-dependent, with no NFD support, and the folding logic would then live in two places that quietly diverge.

## Alternatives considered

**Accent-insensitive collation** (`Latin1_General_CI_AI`). Rejected. It handles `š → s` but cannot transliterate Cyrillic at all, and does not handle `đ → dj`. It also makes matching a property of the database server — invisible in the repository, untestable in a unit test, and potentially different between environments.

**SQL Server Full-Text Search.** Rejected. There is no Serbian word breaker, so stemming and thesaurus features do not apply. It requires the full-text component to be installed, which complicates both the Docker image and Testcontainers-based tests. And it still would not solve transliteration, which is the actual problem.

**Elasticsearch or Meilisearch.** Rejected as disproportionate. It means another service to run, deploy, monitor and keep in sync, for a catalogue in the low thousands of listings. The tiered approach ([search.md](../search.md)) delivers what is needed on the database already in use. This is the alternative to revisit if the catalogue grows by two orders of magnitude.

**Folding at query time only,** with `LIKE` over the raw columns. Rejected: it cannot work. Folding the query does not fold the stored data, so `sisanje` still would not match `Šišanje`. Both sides have to be folded, and folding the stored side on every query means folding every row on every query.
