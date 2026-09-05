# ADR-0008 — Hetzner for production, Azure maintained alongside

**Status:** Accepted

## Context

The application is containerised and needs somewhere to run. Two candidates were built out completely, each with its own CD workflow:

- **Azure App Service** with Azure SQL — a managed platform
- **Hetzner Cloud** — a plain Linux VPS running the same Docker Compose stack used locally

This is a pre-launch product with no revenue. Monthly cost matters more than it will later.

## Decision

**Hetzner is production.** The Azure pipeline is kept working and is not deleted.

## Consequences

**What it buys.**

Cost, by a wide margin. A VPS plus a self-hosted SQL Server container is a fraction of App Service plus Azure SQL at comparable capacity, and the difference is decisive at zero revenue.

Portability. The server runs the *same* `docker-compose.yml` used locally, with a small production override. Nothing is tied to a provider API, so the same workflow deploys to any VPS anywhere. That was the point of containerising in the first place.

No lock-in on managed services. The stack is SQL Server, Redis and a .NET container — all standard, all movable.

**What it costs.**

You become the platform. Patching, firewall, TLS certificates, backups, monitoring and disk space are all your responsibility. `docker image prune -f` runs after every deploy specifically because images accumulate and fill the disk, which is exactly the class of problem a managed platform absorbs for you.

No managed backups. This is the most serious consequence and it is not yet fully solved — SQL Server data lives in a named Docker volume on one host.

No autoscaling and no built-in slot-based deploys. Rollback is manual: edit `IMAGE_TAG` in `.env` and re-run `up -d`. Adequate, and documented in the failure output of the deploy job so nobody has to look it up during an incident.

Maintaining two pipelines is ongoing work. Both are exercised, so both keep working, but every change to the deployment shape has to be made twice.

**Why keep Azure at all.**

Three honest reasons:

1. **It is a real, working migration path.** If managed infrastructure becomes worth paying for, the pipeline already exists and has been proven — including the non-obvious parts, such as `azure/login` being required in the migration job so `azure/sql-action` can open the SQL firewall for the runner's random IP.
2. **It demonstrates a different deployment model.** The Azure pipeline runs migrations as a **separate job before deploy**, using an idempotent script, rather than letting the starting container migrate. That is the correct pattern for a managed platform where several instances may start at once, and it is worth having built.
3. **It is portfolio-relevant.** Azure appears in job descriptions in a way that "a VPS with Docker Compose" does not, and having genuinely built and run it is different from having read about it.

Reason 3 is a real reason, and stating it plainly is better than dressing it up as a technical one.

## Alternatives considered

**Azure App Service as production.** Rejected on cost, at this stage only. The trade is worth revisiting once there is revenue, and the pipeline is kept ready precisely so that revisit is cheap.

**Kubernetes.** Rejected as disproportionate. A single service, a database and a cache do not need an orchestrator; Compose already handles multiple instances on one host ([scaling.md](../scaling.md)), and the operational cost of Kubernetes exceeds the problem it would solve here.

**Delete the Azure workflow.** Rejected: an untested migration path is not a migration path, and the parts of it that are hard to get right are exactly the parts that are already solved.

**Managed database with self-hosted app.** A middle option that directly addresses the backup gap. Not adopted yet; it is the most likely next change if the current setup shows strain.
