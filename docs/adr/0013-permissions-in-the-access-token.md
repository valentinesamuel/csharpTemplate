# ADR-0013: Permissions in the access token

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Endpoints authorize on fine-grained permissions (`<resource>.<action>`, for example `users.manage`) rather than role names, so roles can be reshaped without code changes. The permission check runs on every request and must not depend on Redis, the database or any network call (INV-13).

## Decision

Each module declares its permissions as `PermissionDefinition(name, description, defaultRoles)` with constants in its Contracts (for example `src/Modules/Auth/Acme.Service.Modules.Auth.Contracts/AuthPermissions.cs`). `migrate` syncs the catalog and the seed roles `Admin`, `Staff` and `Customer`. When a token is issued, the user's permissions are computed from their roles and written as `perm` claims. Endpoints call `RequirePermission("<name>")`; a dynamic policy provider creates `perm:<name>` policies that succeed when the claim is present (`src/BuildingBlocks/Acme.Service.BuildingBlocks.Web/Authorization/`).

## Options considered

### Option A: permissions as claims in the access token (chosen)

- Pros: stateless and fast; no dependency that could fail open or make authorization unavailable; easy to test.
- Cons: permission changes take effect at the next refresh, up to one access-token lifetime (15 minutes); tokens grow with the number of permissions.

### Option B: roles in the token, permissions resolved per request

- Pros: changes apply immediately.
- Cons: a database or cache lookup on every request; a cache outage forces fail-open or fail-closed for everyone.

## Consequences

- After an admin assigns or removes a role, the user gets the new permissions at the next refresh. For urgent revocation, lock the user: that revokes all sessions, and `live-session` endpoints reject the old access tokens immediately.
- Admin and other sensitive endpoints combine `perm:<name>` with the `live-session` policy.
- Ownership rules (for example "own resources unless the caller has a manage permission") are checked in the use case, not by the policy.
- There is no default admin account; bootstrap the first Admin with `auth:grant-role --email <e> --role Admin`.
- Every module's permissions must be added to the Auth catalog tests.

## Related invariants

- INV-13: stateless authorization on the `perm` claim.
- INV-12: every endpoint declares its authorization or is explicitly anonymous.
