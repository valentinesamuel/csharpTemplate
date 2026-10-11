# ADR-0005: Auth module uses ASP.NET Core Identity and EF Core directly

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Non-Auth modules keep EF Core inside their `Persistence` namespace and reach data only through `IRepository` and `IUnitOfWork` ports ([ADR-0016](0016-ef-confinement-to-persistence-namespace.md), INV-04). The Auth module is built on ASP.NET Core Identity, whose `UserManager`, `SignInManager`, user store and token providers are designed around an EF Core `DbContext` and are used throughout the Auth features: login, lockout, confirmation and reset tokens, MFA keys and recovery codes. Data Protection keys are also persisted with EF Core in the `auth` schema. Wrapping all of that behind neutral ports would mean re-implementing Identity's store contracts for no practical gain: an Auth module on another store would replace Identity's storage anyway.

## Decision

Grant the Auth module a documented exception (INV-05): it may depend on EF Core, ASP.NET Core Identity and `BuildingBlocks.Persistence` anywhere inside `src/Modules/Auth/Acme.Service.Modules.Auth/`. The exception is scoped by assembly name in the architecture tests and does not extend to any other module.

What stays forbidden for Auth:

- direct `Npgsql` types (rule R3 applies to Auth like every module; only the EF-generated `Persistence.Migrations` namespace is exempt, as for all modules), so advisory locks in the seeder use SQL through EF instead;
- making anything other than `AuthModule` public; other modules still see only `src/Modules/Auth/Acme.Service.Modules.Auth.Contracts/`;
- mapping outside the `auth` schema.

Identity is configured with `AddIdentityCore` (not `MapIdentityApi`), Guid keys, a hand-mapped snake_case model on `AuthDbContext : ModuleDbContext`, and a custom `AuthUserStore` that encrypts the authenticator key with Data Protection and hashes recovery codes.

## Options considered

### Option A: exception for Auth only (chosen)

- Pros: uses Identity as designed (password hashing, lockout, token providers, security stamps); less custom security code; the exception is explicit, tested and narrow.
- Cons: Auth cannot move to a non-relational store without replacing Identity's storage; reviewers must remember that INV-04 does not apply to Auth.

### Option B: Identity behind repository ports

- Pros: uniform rule for every module.
- Cons: re-implements large parts of Identity's store interfaces; more code in the most security-sensitive module; no realistic benefit.

### Option C: no Identity, hand-written user management

- Pros: full control.
- Cons: reinvents password hashing, lockout and token generation, with a high risk of security bugs.

## Consequences

- `tests/Acme.Service.ArchitectureTests/ArchRules.cs` exempts the Auth assembly by name from R4 (EF confinement) and keeps R3 (no `Npgsql`, no provider SDKs) in force.
- Some Auth inputs (login, refresh, MFA steps) are not `ICommand` because failed attempts must be persisted ([request lifecycle](../architecture/request-lifecycle.md)).
- Identity, Data Protection persistence, the seeder and `auth:grant-role` are registered in `IModule.AddPersistence`, so the minimal command hosts get them without JWT secrets.
- Any future exception for another module needs its own ADR.

## Related invariants

- INV-05: the documented Auth exception, scoped to the Auth assembly.
- INV-04: persistence confinement, unchanged for every other module.
- INV-02: Auth internals remain internal.
- INV-28: TOTP keys encrypted and recovery codes hashed at rest.
