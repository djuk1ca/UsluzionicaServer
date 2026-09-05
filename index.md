# Uslužionica API

Backend for a Serbian services marketplace — where clients find providers, agree on the job, and pay part of it in platform tokens.

ASP.NET Core 8 Web API with 77 REST endpoints, two SignalR hubs, EF Core against SQL Server, Redis for caching and coordination, and a search engine built for Serbian text.

<div class="row">
<div class="col-md-6">

### Start here

- [Architecture](docs/architecture.md) — layers, startup, request lifecycle
- [Domain model](docs/domain-model.md) — 20 entities and the rules they encode
- [API reference](docs/api-reference.md) — every endpoint, with access levels

</div>
<div class="col-md-6">

### The interesting parts

- [Search engine](docs/search.md) — four tiers, diacritics, Cyrillic, typos
- [Token economy](docs/token-economy.md) — earning, spending, anti-abuse
- [Scaling & caching](docs/scaling.md) — Redis that never becomes a dependency
- [Decisions](docs/adr/README.md) — eight ADRs with the reasoning

</div>
</div>

---

## At a glance

| | |
|---|---|
| **77 REST endpoints** | across 15 controllers, JWT-secured, role-aware |
| **2 SignalR hubs** | encrypted chat and push notifications over WebSockets |
| **20 EF Core entities** | 10 migrations, SQL Server, 188 seeded service categories |
| **4-tier search** | diacritic-, script- and typo-tolerant Serbian text search |
| **207 tests** | 82.5% line coverage, gated in CI, real SQL Server via Testcontainers |
| **2 deploy targets** | Hetzner (production) and Azure App Service, both automated |

## Code reference

The [code reference](xref:UsluzionicaServer.Services) is generated directly from the source. Note that inline documentation comments are written in **Serbian**, the project's working language — the hand-written documentation above is the English counterpart.

## Repository

[github.com/djuk1ca/UsluzionicaServer](https://github.com/djuk1ca/UsluzionicaServer)

Copyright © 2026 Đurađ Manojlović. All rights reserved.
