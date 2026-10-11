# ADR-0015: Test libraries and licence policy

> Applies to: Acme.Service template

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

Several popular .NET libraries moved to commercial or restrictive licences (MediatR, AutoMapper, FluentAssertions from v8, Moq after its telemetry incident, MassTransit from v9). A template is copied into many services, so a licence problem multiplies. Tests must also run against real infrastructure (PostgreSQL, Redis, S3-compatible storage, Azurite, SMTP) without shared environments, and the .NET 10 SDK changed how test runners integrate with `dotnet test`.

## Decision

- **Test stack:** xUnit v3 on the VSTest runner (`xunit.v3.mtp-off`), Shouldly for assertions, NSubstitute for test doubles (hand-written fakes for internal interfaces, which NSubstitute cannot proxy), Testcontainers for real dependencies, `Microsoft.AspNetCore.Mvc.Testing` for in-process hosts, NetArchTest.Rules for architecture rules, and a hand-written `ManualTimeProvider` for time (a recorded P6 deviation from D-17, which named FakeTimeProvider).
- **Commands:** unit tests `dotnet test Acme.Service.slnx -c Release --filter "Category!=Integration"`; integration tests (Docker required) with `--filter "Category=Integration"`. Integration tests carry `[Trait("Category","Integration")]`.
- **Shared fixtures** live in `tests/Acme.Service.Testing/`, including provider contract suites in `tests/Acme.Service.Testing/Contracts/` that every provider implementation must pass.
- **Licence policy:** no commercially licensed packages. MediatR, AutoMapper, FluentAssertions (any version; stricter than INV-25, which names v8+, a deviation recorded in state.md at P18), Moq and MassTransit v9+ are forbidden and checked by rule R9 in `tests/Acme.Service.ArchitectureTests/ForbiddenPackagesTests.cs` across `Directory.Packages.props`, every csproj and every lock file. Copyleft (LGPL) dependencies need an ADR ([ADR-0010](0010-hangfire-with-postgres-storage.md)).
- **Supply chain:** Central Package Management in `Directory.Packages.props` with transitive pinning, no `Version` on any `PackageReference`, committed `packages.lock.json` files restored with `--locked-mode` in CI, NuGet audit failing on high and critical advisories, and no prerelease packages.

## Options considered

### Option A: xUnit, Shouldly, NSubstitute, Testcontainers, NetArchTest (chosen)

- Pros: permissive licences (Apache-2.0, BSD-3-Clause, MIT); real dependencies in tests; architecture rules as ordinary tests.
- Cons: Testcontainers needs Docker locally and in CI; integration runs take minutes.

### Option B: FluentAssertions and Moq

- Pros: widely known.
- Cons: licence and supply-chain concerns; excluded.

### Option C: in-memory database providers instead of containers

- Pros: fast, no Docker.
- Cons: behaviour differs from PostgreSQL (transactions, `SKIP LOCKED`, advisory locks, `xmin`), so the important failure modes would go untested.

### Runner alternatives

Full Microsoft.Testing.Platform mode was not adopted because it changes the `dotnet test` command line and coverage collection; the VSTest-compatible xUnit v3 package keeps the standard commands.

## Consequences

- Test names follow `Method_Scenario_Expected`.
- Destructive resilience tests own their containers; asynchronous effects are polled with timeouts, never fixed sleeps.
- Every package's licence is recorded when it is added; a forbidden package fails the architecture tests.
- Test-only container images may have their own licences (for example the Redis image); they are never shipped.

## Related invariants

- INV-25: licence hygiene.
- INV-20: Central Package Management and locked restores.
- INV-21: clean build with warnings as errors.
