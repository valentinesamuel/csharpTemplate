# ADR-0014: External identity provider via OIDC with code exchange, no auto-link

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Some services need "sign in with the company identity provider" next to local accounts. The template must stay vendor-neutral, must not put tokens in URLs, and must not let a malicious or misconfigured identity provider take over an existing local account by asserting its email address.

## Decision

Provide one generic OpenID Connect slot in the Auth module, disabled by default (`Auth:External:Oidc:Enabled=false`; `src/Modules/Auth/Acme.Service.Modules.Auth/External/`). When enabled:

1. `GET /api/v1/auth/external/oidc/challenge?returnUrl=` starts the authorization code flow with PKCE. `returnUrl` must exactly match an entry of `Auth:External:Oidc:AllowedReturnUrls`, else 400.
2. The provider returns to the OIDC handler, which signs in a short-lived external cookie; `GET /api/v1/auth/external/oidc/callback` then reads it.
3. If the external login is already linked, that user signs in. If no local account has the email, a user is provisioned (email confirmed only when the provider says `email_verified=true`) with the default role. If a local account has the email but no link, the callback redirects to `returnUrl?error=account_exists` and links nothing.
4. On success the callback stores a one-time code (32 random bytes, SHA-256 hashed, valid 60 seconds), signs out the external cookie and redirects to `returnUrl?code=...`.
5. The client calls `POST /api/v1/auth/external/exchange` with the code and gets the service's own tokens (`amr=[ext]`), or an MFA challenge if the local user has MFA enabled.

When disabled, the routes and the OIDC scheme are not registered.

## Options considered

### Option A: generic OIDC handler with a one-time code exchange (chosen)

- Pros: works with any compliant provider; no access or refresh token in a URL; the code is useless after 60 seconds or one use; local and external sign-ins issue the same tokens.
- Cons: one extra round trip for the client; the external cookie needs HTTPS for cross-site redirects.

### Option B: a vendor-specific handler

- Pros: richer provider features.
- Cons: ties the template to one vendor.

### Option C: run an identity server (for example Keycloak) in compose

- Pros: realistic local testing.
- Cons: heavy for every developer; deferred to an optional future profile.

### Linking alternatives

Auto-linking on a verified email was rejected: it lets any provider that can assert an email address take over a local account.

## Consequences

- Linking an external identity to an existing account needs an explicit, authenticated flow (not provided in v1).
- Startup fails when the slot is enabled with incomplete configuration (authority, client id and secret, at least one return URL, `openid` scope; HTTPS authority outside Development).
- If the provider is down only external sign-in fails (`external_provider_unavailable`); password sign-in is unaffected.
- The three external routes are in the anonymous allowlist and rate limited where they issue tokens.

## Related invariants

- INV-14: external login codes can never be used as bearer tokens.
- INV-12: the external routes are explicitly anonymous and allowlisted.
- INV-28: codes and tokens are stored hashed and never logged.
- INV-17: incomplete configuration fails startup.
