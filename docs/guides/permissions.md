# Permissions

> Applies to: Acme.Service template

Authorization is role-based with fine-grained permissions. Roles hold permissions; users hold roles; the access token carries the user's permissions as `perm` claims, and endpoints check them without any database or cache lookup ([ADR-0013](../adr/0013-permissions-in-the-access-token.md)).

## The catalog

Permissions are lower-case dotted `<resource>.<action>` names. Each module declares its own in its Contracts project and returns them from `IModule.Permissions` as `PermissionDefinition(name, description, defaultRoles)`.

| Permission | Owner | Default roles | Grants |
| --- | --- | --- | --- |
| `users.read` | Auth | Admin, Staff | `GET /api/v1/auth/users/{id}` |
| `users.manage` | Auth | Admin | Assign and unassign roles, lock and unlock users |
| `roles.read` | Auth | Admin, Staff | `GET /api/v1/auth/roles` |

<!-- <sample:orders> -->

| Permission | Owner | Default roles | Grants |
| --- | --- | --- | --- |
| `orders.read` | Orders | Admin, Staff, Customer | Read own orders; with `orders.manage`, every order |
| `orders.create` | Orders | Admin, Customer | Place orders, change lines of own pending orders, upload attachments to own orders |
| `orders.cancel` | Orders | Admin, Customer | Cancel own pending orders |
| `orders.manage` | Orders | Admin, Staff | Every order: read, change, confirm, cancel, delete |

<!-- </sample:orders> -->

The Auth definitions are in `src/Modules/Auth/Acme.Service.Modules.Auth/Seeding/PermissionCatalog.cs` (constants in `src/Modules/Auth/Acme.Service.Modules.Auth.Contracts/AuthPermissions.cs`).

## Roles and the seeder

- The seed roles are `Admin`, `Staff` and `Customer`. They are code-owned: there is no endpoint to create, rename or delete a role, and no endpoint to change a role's permissions.
- Every `migrate` run executes the Auth seeder after the migrations. It upserts every declared permission into `auth.permissions`, deletes permissions that are no longer declared (their grants go with them), makes sure the seed roles exist, and sets each seed role's permissions to exactly the declared defaults. Users' role memberships are never touched.
- A permission's default roles must be seed roles; a definition naming any other role, a malformed or duplicate name, or an empty description makes `migrate` fail before anything is written.
- Self-registered and externally provisioned users get `Auth:DefaultRole` (`Customer`).
- Grant roles with the admin endpoints or, for the first administrator, with `auth:grant-role` ([auth guide](auth.md#bootstrap-the-first-administrator)).

## How a request is authorized

1. JWT bearer validation (signature, `kid`, `alg`, `typ`, issuer, audience, lifetime). No network or database access.
2. The authorization fallback policy requires an authenticated user for every endpoint that is not explicitly anonymous.
3. `.RequirePermission("<resource>.<action>")` (from `src/BuildingBlocks/Acme.Service.BuildingBlocks.Web/Authorization/EndpointAuthorizationExtensions.cs`) adds the policy `perm:<name>`, which passes when the token has a matching `perm` claim. Missing permission: 403. The name's format is validated when the endpoint is mapped, so a malformed name fails at startup; a well-formed name that no module declares is not detected and simply never passes.
4. Endpoints that also need a live session add `.RequireAuthorization("live-session")`.
5. Ownership ("only my own records") is not a permission: the use case checks it and answers 404 rather than 403 for records the caller may not see, so ids cannot be probed.

Permissions are computed from the user's roles when a token is issued. A role or permission change therefore reaches the user at the next refresh, at most `Auth:Jwt:AccessTokenLifetime` (15 minutes) later. To cut off a user immediately, lock the account: refresh stops working at once and the live-session endpoints reject the user's tokens.

## Adding a permission

1. Add a constant to the module's `<Module>Permissions` class in its Contracts project, for example `public const string Export = "invoices.export";`, and a `PermissionDefinition` with a description and the seed roles that get it by default.
2. Make sure the module's `IModule.Permissions` returns the definition.
3. Protect the endpoint: `.RequirePermission(InvoicesPermissions.Export)`.
4. Run `migrate` (locally, in Compose or as the Kubernetes Job). Until the seeder has run, no role holds the new permission, so callers get 403.
5. Users get the permission in their next token.

Renaming a permission is a delete plus an add: the seeder removes the old name with its grants and adds the new one with its defaults. Because only seed roles exist and their permissions are reset on every `migrate`, there are no hand-made grants to lose. The seeder runs in the migration Job, before the old pods are gone, so rename across two releases: add the new name (keeping the old one) in release N, remove the old one in N+1 ([migrations](../operations/migrations.md#expandcontract)).

## Requiring one of several permissions

`RequirePermission` checks a single permission. For "owner with permission A, or anyone with permission B", require an authenticated user holding any of the permissions and check ownership in the use case.

<!-- <sample:orders> -->

The Orders sample does exactly this with a module-local helper, `RequireAnyPermission` in `src/Modules/Orders/Acme.Service.Modules.Orders/Features/Shared/OrdersHttp.cs`: for example cancel requires `orders.cancel` or `orders.manage`, and the use case allows `orders.cancel` holders only on their own orders.

<!-- </sample:orders> -->

## Testing permissions

- Integration tests obtain real tokens through the Auth API and grant roles with the same code path as `auth:grant-role`; see [testing](testing.md).
- `tests/Acme.Service.Modules.Auth.Tests/Seeding/AuthSeederTests.cs` covers the seeder (idempotent, removed permissions deleted).
- `tests/Acme.Service.IntegrationTests/Security/AuthNegativeTests.cs` checks that a missing `perm` claim gives 403.
