# Testing

> Applies to: Acme.Service template

Tests use xUnit v3 (run through VSTest), Shouldly for assertions, NSubstitute for substitutes, Testcontainers for real PostgreSQL, Redis, S3 (RustFS), Azurite and Mailpit, `Microsoft.AspNetCore.Mvc.Testing` for in-process hosts and NetArchTest for architecture rules ([ADR-0015](../adr/0015-test-and-licence-choices.md)). FluentAssertions and Moq are not allowed.

## Commands

| What | Command |
| --- | --- |
| Unit and architecture tests (no Docker) | `dotnet test Acme.Service.slnx -c Release --filter "Category!=Integration"` |
| Integration tests (Docker running) | `dotnet test Acme.Service.slnx -c Release --filter "Category=Integration"` |
| Architecture tests only | `dotnet test Acme.Service.slnx -c Release --filter "FullyQualifiedName~ArchitectureTests"` |
| One project | `dotnet test tests/Acme.Service.Modules.Auth.Tests -c Release` |
| One test | `dotnet test Acme.Service.slnx -c Release --filter "FullyQualifiedName~RefreshTokenTests"` |
| With coverage | add `--collect "XPlat Code Coverage"` (coverlet; Cobertura files under `TestResults/`) |

CI runs the first two (with TRX loggers and coverage) on every pull request (`.github/workflows/ci.yml`); the integration step is retried once and the retry is reported. Format is checked with `dotnet format Acme.Service.slnx --verify-no-changes`.

## Test projects

| Project | Contents |
| --- | --- |
| `tests/Acme.Service.Testing/` | Shared fixtures, not a test project: `ServiceWebFactory`, container fixtures in `Containers/`, provider contract suites in `Contracts/`, `CapturingNotificationSender`, runtime-generated signing keys. |
| `tests/Acme.Service.BuildingBlocks.Tests/` | Use-case pipeline, results, paging, web glue, persistence, outbox, inbox, locks. |
| `tests/Acme.Service.Infra.Tests/` | Every provider against its contract suite and its failure behaviour. |
| `tests/Acme.Service.Modules.Auth.Tests/` | Tokens, sessions, account flows, MFA, passwords, admin, external login, seeding, CLI. |
| `tests/Acme.Service.IntegrationTests/` | End-to-end journey, provider swaps, security negatives, resilience (Redis down, Auth database down, PostgreSQL blips), the anonymous endpoint allowlist, `migrate` and readiness. |
| `tests/Acme.Service.ArchitectureTests/` | Boundary rules R1 to R10, the single-schema check, the template token scan, and the proof that an illegal reference fails (with `tests/Acme.Service.ArchitectureTests.Fixtures.IllegalReference/`). |

<!-- <sample:orders> -->

| Project | Contents |
| --- | --- |
| `tests/Acme.Service.Modules.Orders.Tests/` | The sample: domain, endpoints, list queries, concurrency, idempotency, attachments, messaging and jobs. |

<!-- </sample:orders> -->

## Conventions

- Name tests `Method_Scenario_Expected`.
- Mark every test that needs Docker with `[Trait("Category", "Integration")]`, so the unit run stays fast and offline.
- Inject `TimeProvider` in production code and drive time in tests with a manual time provider (the test projects contain `ManualTimeProvider` fixtures); never sleep for a fixed time. For asynchronous effects (outbox delivery, jobs) poll with a timeout.
- Never commit key material: tests generate signing keys at run time (`tests/Acme.Service.Testing/Security/TestSigningKeys.cs`).
- Test projects are executables (`OutputType` `Exe`, required by xUnit v3) and cannot reference each other; share code through `tests/Acme.Service.Testing/`.

## Host-level tests

`tests/Acme.Service.Testing/ServiceWebFactory.cs` starts the real Host in-process in the `Testing` environment with in-process providers, a generated signing key and placeholder email links. Derive from it and override `ConfigurationOverrides` to supply a connection string or switch providers:

```csharp
// connectionString comes from PostgresFixture.CreateDatabaseAsync: a fresh database per test class
public sealed class InvoicesWebFactory(string connectionString) : ServiceWebFactory
{
    protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides => new Dictionary<string, string?>
    {
        ["ConnectionStrings:Postgres"] = connectionString,
    };
}
```

Working examples are the module test fixtures (`tests/Acme.Service.Modules.Auth.Tests/Fixtures/`). Typical flow:

1. Start PostgreSQL once per test class with `PostgresFixture` (Testcontainers) and create a database per test class with `CreateDatabaseAsync`.
2. Run the migrations through the same runner `migrate` uses, then create the factory.
3. Obtain real tokens through the Auth API: register, confirm (the test notification sender `Capture` records the email), log in. Grant roles with the public `AuthModule.GrantRoleAsync` entry point that `auth:grant-role` uses.
4. Call the endpoint with `HttpClient` and assert status, problem `code` and body.

Startup-failure tests (invalid configuration, bad key ring, dev-only provider in Production) run the Host out of process, because `WebApplicationFactory` hides the real exit code; see `tests/Acme.Service.Modules.Auth.Tests/Fixtures/HostProcess.cs`.

## Contract suites for providers

Each port has an abstract suite in `tests/Acme.Service.Testing/Contracts/`. A provider test derives from it and creates the implementation, so every provider is held to the same behaviour (see [adding a provider](adding-a-provider.md)).

## Security tests you must keep green

- `tests/Acme.Service.IntegrationTests/Security/AnonymousEndpointAllowlistTests.cs` enumerates every endpoint (also with OIDC enabled) and fails on any anonymous endpoint that is not in `tests/Acme.Service.IntegrationTests/Security/AnonymousEndpointAllowlist.cs`. Adding an anonymous endpoint means adding it there with a reason.
- `tests/Acme.Service.IntegrationTests/Security/AuthNegativeTests.cs` covers tampered, expired, wrongly typed and wrongly signed tokens, MFA tokens used as access tokens, refresh reuse, lockout, rate limiting and missing permissions.
- `tests/Acme.Service.IntegrationTests/Resilience/AuthDbDownTests.cs` proves that live-session endpoints fail closed while stateless validation keeps working.

## Architecture tests

They need no maintenance when you add a module or provider: modules are discovered from the project files under `src/`. When a rule fails, the message names the rule and the offending type; fix the code, not the rule. Rules and their invariants are listed in [module boundaries](../architecture/module-boundaries.md).
