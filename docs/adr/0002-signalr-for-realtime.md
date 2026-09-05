# ADR-0002 — SignalR for chat and notifications

**Status:** Accepted

## Context

The product needs two live features: a chat between client and provider, and in-app notifications for bookings, offers, reviews and token events. The client is a .NET MAUI Blazor Hybrid app on Android and iOS, and the server is ASP.NET Core.

Delivery has to work when the recipient is offline, and it has to keep working when more than one API instance is running.

## Decision

SignalR, with two hubs — `/hubs/chat` and `/hubs/notifications` — authenticated by the same JWT the REST API uses, passed as `?access_token=` because a WebSocket handshake cannot send headers.

Notifications are **persisted first and pushed second**. The database row is the source of truth; the push is an optimisation.

## Consequences

**What it buys.**

WebSockets with automatic fallback to Server-Sent Events and long polling, so a restrictive network degrades rather than breaks. A strongly typed client in the same language as the server, since the app is .NET. Group semantics that map directly onto the domain: `user-{id}` for anything addressed to a person, `conversation-{id}` for a room.

Because notifications are written before they are sent, an offline recipient loses nothing — the next `GET /api/notifications` delivers them. The hub never has to guarantee anything.

**What it costs.**

A persistent connection per client, which is state the server has to hold. More importantly, that state is **per process**, and three things break the moment a second instance exists:

| Broken | Fix |
|---|---|
| `Clients.User(...)` cannot reach a connection on another instance — the message vanishes with no error and no log | Redis backplane |
| Presence is computed from local memory, so users on other instances always look offline | `OnlineTracker` on Redis |
| Data Protection keys are per process, so verification links generated on A cannot be read on B | keys persisted to Redis |

All three are handled ([scaling.md](../scaling.md)), but they are real complexity that a polling design would not have. Without the backplane, `docker compose up --scale api=2` silently breaks chat.

There is also a security detail that had to be handled explicitly: the query-string token is only accepted for paths under `/hubs`. Without that guard, every REST endpoint would accept tokens in the URL, and tokens would end up in access logs and browser history.

## Alternatives considered

**HTTP polling.** Rejected. A chat needs sub-second delivery, which means polling every 2–3 seconds from every client. That is constant load for mostly empty responses, and it still feels sluggish. Notification-only polling would have been acceptable; chat is what ruled it out.

**Raw WebSockets.** Rejected: it means hand-writing reconnection, heartbeats, protocol negotiation, group management and a fallback path — all of which SignalR already provides and gets right.

**Firebase Cloud Messaging / APNs only.** Rejected as the primary channel. Push notifications are for when the app is closed; they are best-effort, platform-specific, and cannot carry a live chat. FCM remains the right addition later for background delivery, layered on top of the persisted notification rather than replacing it.

**A hosted service such as Pusher or Ably.** Rejected: a per-connection cost and an external dependency for something the platform already provides, on a project where the ASP.NET stack is a given.
