# ADR-0006: RS256 access tokens, key ring and rotation

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

The Auth module issues the access tokens that every endpoint validates. Validation must be stateless and must never depend on a network call, the database or the cache (INV-13). Keys must be rotatable without an outage, must never be committed, and the template must stay cloud-neutral.

## Decision

Sign access tokens with RS256 and header `typ=at+jwt`, from a key ring configured under `Auth:Jwt:SigningKeys`. Each key has a `Kid`, a `Status` (`Active`, `Next` or `Retiring`) and exactly one key source (`PrivateKeyPath` for a mounted PEM file, or `PrivateKeyBase64`). Exactly one key is `Active` and signs; all keys validate; `GET /.well-known/jwks.json` publishes all public keys with a 5-minute cache. JWT bearer validation resolves keys only from the ring by `kid`, with no authority or metadata fetch (`src/Modules/Auth/Acme.Service.Modules.Auth/AuthenticationSetup.cs`, `src/Modules/Auth/Acme.Service.Modules.Auth/Tokens/SigningKeyRing.cs`).

Rotation is a runbook:

1. Add the new key as `Next` everywhere; wait at least the JWKS cache age.
2. Promote it to `Active` and demote the old key to `Retiring`.
3. Wait at least the access-token lifetime plus clock skew.
4. Remove the old key.

## Options considered

### Option A: key ring from configuration or a secret store (chosen)

- Pros: cloud-neutral; explicit and reviewable; keys come from environment variables (Render environment variables when deployed) or mounted files; rotation without invalidating live tokens.
- Cons: rotation is manual; keys load at startup, so each step needs a rolling restart.

### Option B: keys stored in the database and rotated by the app

- Pros: automatic rotation.
- Cons: key material in the database; validation would depend on the database; more code in the security core.

### Option C: cloud KMS signing

- Pros: keys never leave the KMS.
- Cons: ties the template to one cloud; a network call per signature. Recorded as a possible future ADR.

### Algorithm alternatives

HS256 was rejected because every validator would hold the signing secret. Only RS256 is accepted (`ValidAlgorithms`), which also rules out `alg=none` and key-confusion attacks.

## Consequences

- Startup fails on a misconfigured ring: no or several `Active` keys, duplicate `Kid`, RSA below 2048 bits, both or neither key source.
- An unknown or missing `kid` is rejected, never "try all keys".
- `scripts/dev-env.cs` generates an RSA-3072 key into the gitignored `deploy/compose/.env`; when deployed the keys are Render environment variables in the `PrivateKeyBase64` form (`PrivateKeyPath` stays supported for platforms that mount files).
- A rolling deployment must finish step 1 on all replicas before step 2 starts.

## Related invariants

- INV-11: no key material in the repository, image or logs.
- INV-13: stateless validation; misconfiguration fails startup.
- INV-14: only RS256 `at+jwt` tokens with the configured issuer and audience authenticate.
