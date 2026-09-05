# ADR-0007 — Referral reward paid in two instalments

**Status:** Accepted

## Context

The referral programme pays existing users tokens for bringing in new ones. Tokens have real value inside the platform — they buy listing visibility and fund discount negotiations — so any payout rule is an attack surface.

The obvious design pays the referrer when someone registers with their code. Registration is free, unlimited, and requires nothing but an email address that looks valid. Under that rule, anyone can open accounts with their own referral code and mint tokens indefinitely.

## Decision

Split the reward into two instalments, each triggered by an event that costs something to fake:

| Trigger | Status | Reward |
|---|---|---|
| Invitee **confirms their email address** | `Registered` | 2 tokens |
| Invitee **activates a provider account** | `Rewarded` | 3 tokens |

Neither is paid on registration itself. Both payout methods are idempotent and safe under concurrent calls. `Referral.ReferredUserId` is unique, so a user can be referred exactly once in their lifetime.

## Consequences

**What it buys.**

The first instalment requires a real, unique, working mailbox per account. That is not impossible to farm, but it is no longer free and no longer instant. It also aligns the reward with a threshold that already exists in the system: an account with an unconfirmed email cannot log in at all, so before confirmation it is not yet an account in any meaningful sense.

The second instalment requires the invitee to actually become a provider — the outcome the programme exists to produce. Weighting it higher (3 versus 2) puts the larger reward on the more valuable and more expensive-to-fake action.

Idempotency is not defensive over-engineering here. Verification links are activated twice routinely: a mail client prefetches the URL, then the user clicks it. And password reset is a second path through which an email can become confirmed. Without idempotency, both would double-pay.

**What it costs.**

More state to track. Two nullable columns — `SignupTokensAwarded` and `ActivationTokensAwarded` — rather than one amount, plus their timestamps. Nullable rather than defaulting to zero, deliberately, so that *not paid* is distinguishable from *paid zero*.

Two payout paths means two places that must stay idempotent, and both are covered by tests, including the negative case that fails if someone "simplifies" registration by paying immediately.

The referrer's reward is also delayed and partly outside their control — they may never receive the second instalment. That is accepted: the programme rewards outcomes, not introductions.

## Alternatives considered

**Pay on registration.** Rejected: free and unlimited to fake, which makes it not a referral programme but a token faucet.

**Pay only on provider activation, single instalment.** Rejected: the feedback loop is too long. Someone who invites a friend gets nothing for days or weeks and concludes the feature is broken. The first instalment provides visible confirmation that the referral was registered.

**Pay on the invitee's first completed booking.** Rejected as the primary trigger: a stronger anti-abuse signal, but the delay is measured in weeks and depends on a third party (the provider) confirming and executing. Worth revisiting as a third instalment.

**Require phone verification.** Rejected for now: it is a stronger signal than email, but it adds an SMS provider, a per-message cost, and friction on a market where users are being asked to try something new.

**Cap the number of referrals per user.** Not implemented, and worth noting as a gap. The current defences are per-invitee (unique constraint, real mailbox required) rather than per-referrer. A volume cap or velocity check would complement them.
