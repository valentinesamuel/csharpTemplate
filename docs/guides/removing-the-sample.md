<!-- <sample:orders> -->

# Removing the Orders sample

> Applies to: Acme.Service template

The Orders module (`src/Modules/Orders/`) exists to demonstrate the patterns; most services delete it. This whole page is itself sample content and disappears with it.

## With the init script (recommended)

On a fresh, clean clone:

```bash
dotnet run scripts/init.cs -- --name Contoso.Billing --remove-sample --dry-run   # preview the changes
dotnet run scripts/init.cs -- --name Contoso.Billing --remove-sample
```

`--remove-sample`:

1. deletes `src/Modules/Orders/` (both projects and this module's README) and `tests/Acme.Service.Modules.Orders.Tests/`;
2. removes every region between a `<sample:orders>` and a `</sample:orders>` marker line from every text file, markers included. Markers are whole comment lines (`//` in C#, `#` in shell and YAML, `<!-- -->` in XML and Markdown). That covers the solution file, the Host project file, `src/Host/Acme.Service.Host/Modules/HostModules.cs`, the Orders parts of shared tests, the configuration-key list in `scripts/ci/check-config-keys.sh` and the Orders passages of the documentation; a file left empty, like this page, is deleted;
3. then personalises the template as usual (tokens, renames, next steps).

It refuses to run when a marker region is unbalanced or nested, and changes nothing in that case. The template self-test workflow (`.github/workflows/template-selftest.yml`) runs both variants on every pull request.

Afterwards:

```bash
dotnet restore Contoso.Billing.slnx --force-evaluate
dotnet format Contoso.Billing.slnx
dotnet build Contoso.Billing.slnx -c Release
dotnet test Contoso.Billing.slnx --filter "Category!=Integration"
ls src/Modules          # Auth only
```

What stays: the Auth module, all BuildingBlocks and providers, the architecture tests (they discover modules from the project files and do not depend on Orders), and the generic guides.

## By hand, after init

`scripts/init.cs` only runs on the uninitialised template. If you kept the sample and want it gone later:

1. Find every marker: `grep -rn "sample:orders" . --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj`.
2. Delete each region, marker lines included, and any file that becomes empty.
3. Delete `src/Modules/Orders/` and the Orders test project directory.
4. Run `dotnet restore <your solution> --force-evaluate`, build and test.
5. Existing databases still contain the `orders` schema, including its own migrations history, outbox and inbox tables. Nothing reads it any more, and readiness no longer checks it. Drop it when you are sure the data is not needed:

   ```sql
   DROP SCHEMA orders CASCADE;
   ```

   The `orders.*` permissions disappear from `auth.permissions` (with their role grants) the next time `migrate` runs, because the seeder deletes permissions that no module declares.

6. If Hangfire is in use, delete the `orders-expire-stale-pending` recurring job from the dashboard or storage; its job type no longer exists.

## What the sample demonstrated

Before deleting it, skim its [README](../../src/Modules/Orders/README.md): it maps each pattern (ports-only use cases, EF confinement, ownership rules, outbox, inbox consumers, locked recurring jobs, blob-first uploads, idempotent create, Contracts calls) to a file, and the generic guides refer to the same patterns.

<!-- </sample:orders> -->
