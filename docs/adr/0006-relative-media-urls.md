# ADR-0006 — Media stored as relative paths

**Status:** Accepted

## Context

Avatars, provider cover images and listing galleries are uploaded to `wwwroot/uploads/` and served as static files. The database originally stored the full URL of each file, including scheme and host:

```
https://localhost:7176/uploads/listings/3/abc.jpg
```

That works until the host changes. Moving from a development machine to production, or later to a CDN, breaks every stored image and requires a data migration to fix.

## Decision

Store the relative path only:

```
/uploads/listings/3/abc.jpg
```

Compose the absolute URL at **serialization time** from `App:BaseUrl`, using a `System.Text.Json` type-info modifier (`MediaUrlJsonModifier`) that rewrites media fields on the way out.

`MediaUrls.ToRelative` is idempotent and tolerates a full URL, so legacy data and new data flow through the same code path. The migration `NormalizeMediaUrlsToRelative` converted the existing rows.

## Consequences

**What it buys.**

The domain moves without touching data. Changing `App:BaseUrl` is a configuration change; moving to a CDN later is the same configuration change. The database holds a fact about where a file lives within the application, not a fact about where the application is deployed — which is the correct separation.

It also removes a whole class of environment bug where a developer's local URLs end up in a shared database.

**What it costs.**

The transformation has to be applied everywhere responses are produced, and there are **two independent serializers**. MVC has its own, and SignalR has its own. Registering the modifier only on `AddControllers` left `MessageDto.SenderImageUrl` carrying a relative path over the hub — an avatar that renders in the conversation list and breaks inside the chat. Both registrations are required:

```csharp
builder.Services.AddControllers().AddJsonOptions(/* modifier */);
builder.Services.AddSignalR().AddJsonProtocol(/* same modifier */);
```

Any future serialization surface — a webhook payload, a background email, a third protocol — has to remember this. That is a real, recurring cost, and it is the reason the modifier is a shared factory rather than duplicated configuration.

`App:BaseUrl` also becomes a required, correctness-affecting setting. If it is wrong in production, every image URL is wrong. `.env.example` and the Compose files call this out explicitly.

## Alternatives considered

**Keep absolute URLs and migrate on every domain change.** Rejected: a data migration for what is a configuration concern, repeated every time the deployment moves.

**Compose the URL in each DTO mapping.** Rejected: dozens of call sites, each one able to forget, and no compiler help when a new DTO with an image field appears.

**Return relative paths and let the client prepend the base URL.** Rejected: every client would have to know the rule, and each would implement it slightly differently. The API should return usable URLs.

**Store files in object storage from the start and keep the full URL.** Not rejected on merit — it is where this is heading. Relative paths are what make that move a configuration change rather than a migration, so this decision is a prerequisite for it rather than an alternative to it.
