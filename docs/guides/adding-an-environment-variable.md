# Adding an environment variable

> Applies to: Acme.Service template

How to add one new setting end to end: the options class that reads it, every file that must carry a value (local, Docker Compose, the deployment platform, the secret store), the documentation and the drift check, and how to prove it works. The worked example is a message-broker address, `KAFKA_BROKER_URL`.

The Kafka code in this guide is illustrative. The repository contains no Kafka client, no `Kafka` options class and no `Kafka:*` key; copy the shape, not the names. The real pattern to copy is `OrdersOptions` (`src/Modules/Orders/Acme.Service.Modules.Orders/Options/OrdersOptions.cs`) and the host's `ObservabilityOptions` (`src/Host/Acme.Service.Host/Configuration/ObservabilityOptions.cs`).

For the list of existing settings see the [configuration reference](configuration.md). For how secrets are delivered see [secrets](../operations/secrets.md).

## 1. Mental model: three names for one value

One value has up to three names, and each layer only understands its own:

| Layer | Name | Who reads it |
| --- | --- | --- |
| Compose `.env` file | `KAFKA_BROKER_URL` | Docker Compose, for `${...}` substitution in `compose.yaml` only |
| Process environment variable | `Kafka__BrokerUrl` | .NET's environment-variable configuration source |
| Configuration key | `Kafka:BrokerUrl` | Your code, through `IConfiguration` and the options class |

.NET turns an environment variable into a key by replacing each `__` (two underscores) with `:`. A raw `KAFKA_BROKER_URL` becomes the key `KAFKA_BROKER_URL`, which no options class binds, so it is silently ignored. The `.env` name is a Compose convenience: `compose.yaml` must map it into the app's `environment:` block under the `Kafka__BrokerUrl` name. A deployment platform has no `.env` layer: its environment key is already the double-underscore name.

Configuration sources, later ones winning (full detail in [configuration](configuration.md#how-configuration-is-loaded)):

1. `src/Host/Acme.Service.Host/appsettings.json`
2. `appsettings.Development.json` when the environment is `Development`
3. User secrets when the environment is `Development`
4. Environment variables
5. Command-line arguments (`--Kafka:BrokerUrl=...`)

So an environment variable overrides both JSON files, and the JSON files are only for defaults and local development.

## 2. Decide: secret or not, and where each value lives

Ask one question: would publishing this value let someone do something they should not? A bare broker host and port is not a secret. A broker URL with a user name and password in it, a SASL password or an API key is.

| | Non-secret (`Kafka__BrokerUrl=broker:9092`) | Secret (SASL password, URL with credentials) |
| --- | --- | --- |
| Safe default for every environment | `appsettings.json` (only if one value suits all environments) | Never in any `appsettings*.json` |
| Local `dotnet run` | `appsettings.Development.json` | User secrets |
| Docker Compose | `deploy/compose/.env.example` (name and a sample value) plus the app's `environment:` block in `deploy/compose/compose.yaml` | The gitignored `deploy/compose/.env`, mapped in `compose.yaml` like any other variable. Add a `CHANGE_ME` placeholder to `.env.example` |
| Deployed | `render.yaml` (`value:`), or the Render dashboard, one value per environment | A `sync: false` entry in `render.yaml`, with the value entered in the Render dashboard. Never a committed value or an `appsettings*.json` file |

Deployed secrets are plain environment variables on the Render service: `render.yaml` declares the key with `sync: false` and you enter the value once in the dashboard, so the value is never in the repository or the image. Changing one needs a redeploy ([secrets](../operations/secrets.md#render-environment-variables)). The rule that holds everywhere (INV-11): no secret in `appsettings*.json`, a deployment manifest, a build argument or the image.

## 3. Worked example: `KAFKA_BROKER_URL`

Goal: a required, non-secret setting `Kafka:BrokerUrl` that must be an absolute URL such as `kafka://broker:9092`, with a local default and a different value per deployed environment.

### Step 1: the options class

Copy the `OrdersOptions` shape: a `SectionName` constant, properties with defaults, and a static `Register` that binds, validates with a message that names the full key, and calls `ValidateOnStart` (INV-17). This is a new, illustrative file; where it lives depends on who owns the setting (a module's `Options/` folder, or `src/Host/Acme.Service.Host/Configuration/` for a host-level setting like `HttpOptions` and `ObservabilityOptions`).

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Host.Configuration;

/// <summary><c>Kafka</c>: message broker connection. Validated on start (INV-17).</summary>
internal sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary><c>Kafka:BrokerUrl</c>: absolute URL of the broker. Required.</summary>
    public string BrokerUrl { get; set; } = string.Empty;

    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(SectionName))
            .Validate(
                o => Uri.TryCreate(o.BrokerUrl, UriKind.Absolute, out _),
                $"{SectionName}:{nameof(BrokerUrl)} is required and must be an absolute URL.")
            .ValidateOnStart();
    }
}
```

Use `$"{SectionName}:{nameof(BrokerUrl)}"` rather than a literal so the failure message always names the real key.

### Step 2: call `Register` in the host

Each options class is registered from the host's composition code. The two existing host options show where:

- `ObservabilityOptions.Register(builder.Services, builder.Configuration);` in `src/Host/Acme.Service.Host/Observability/ObservabilityExtensions.cs` (`AddObservability`).
- `HttpOptions.Register(services, configuration);` in `src/Host/Acme.Service.Host/Http/ForwardedHeadersSetup.cs`.

Add `KafkaOptions.Register(builder.Services, builder.Configuration);` in the extension method that wires up the feature that uses it, alongside those calls (`Program.cs` calls `builder.AddObservability()`), and make sure that method is called from `Program.cs`. `ValidateOnStart` only runs for options that are registered, so a class that is never registered never validates.

### Step 3: local development default

Add the section to `src/Host/Acme.Service.Host/appsettings.Development.json`, which holds the local, non-secret values (a local provider, a password-less connection string):

```json
  "Kafka": {
    "BrokerUrl": "kafka://localhost:9092"
  },
```

Do not add it to `appsettings.json` unless one value is a safe default for every environment. Leaving it out there is what makes production fail fast when nobody supplied it.

### Step 4: Docker Compose

Two edits, both required.

1. `deploy/compose/.env.example`: add the variable under "Behaviour switches", the block that already holds `BLOB_PROVIDER`, `AUTH_CONFIRM_EMAIL_URL` and `OTLP_ENDPOINT`, as `NAME=value` with a comment:

   ```bash
   # Kafka broker URL for the app container.
   KAFKA_BROKER_URL=kafka://kafka:9092
   ```

   People create their own `deploy/compose/.env` from this template (`dotnet run scripts/dev-env.cs`), so existing checkouts need the line added to their `.env` too.

2. `deploy/compose/compose.yaml`: in the `app` service's `environment:` block (the one holding `Auth__Links__ConfirmEmailUrl` and `Observability__Otlp__Endpoint`), map the `.env` name to the .NET name. Follow the existing line format, `Section__Key: ${ENV_NAME:-default}`:

   ```yaml
         Observability__Otlp__Endpoint: ${OTLP_ENDPOINT:-}
         Kafka__BrokerUrl: ${KAFKA_BROKER_URL:-kafka://kafka:9092}
   ```

   Use `${KAFKA_BROKER_URL:?message}` instead of a default when the value must not be guessed. The `app` service also has `env_file: .env`, so the raw `KAFKA_BROKER_URL` is present in the container, but as noted in section 1 .NET ignores it; only the mapped `Kafka__BrokerUrl` counts. The one-shot `migrate` service has its own `environment:` block and does not need the value unless it reads the setting.

### Step 5: the deployment platform

Set the value in the deployment platform's environment: the compose `environment:` block (as in step 4, with the production address), or the equivalent in your platform. The key is the `__` name and the value is a plain string:

```yaml
Kafka__BrokerUrl: kafka://kafka.dev.example.com:9092
```

Use a different value per environment where it differs. Quote values that YAML would otherwise read as a number or boolean (`"9092"`, `"true"`). Environment variables are read at start, so restart the instances after a change.

### Step 6 (variant): the URL embeds credentials, or you need a SASL password

If the value contains a password or token (`kafka://user:pass@broker:9092`, or a separate `Kafka:Sasl:Password`), it is a secret:

- Do not put the value in `render.yaml`, a committed manifest or `appsettings*.json`. Remove it from any of those.
- Add it to `render.yaml` as `- key: Kafka__BrokerUrl` with `sync: false` (or `Kafka__Sasl__Password`), then enter the value in the Render dashboard. .NET reads it exactly like any other environment value. See [secrets](../operations/secrets.md#render-environment-variables).
- Locally, use user secrets; for Compose, put the real value in the gitignored `deploy/compose/.env` and a `CHANGE_ME` placeholder in `.env.example`.
- Never log the value. Validate its shape, but keep the message to the key name.
- Add the key to the secret list in [secrets](../operations/secrets.md) and mark it `Yes` in the Secret column of the configuration reference.

### Step 7: the configuration reference

Add a row to the right table in [configuration](configuration.md), in the same columns as its neighbours (Key, Environment variable, Default, Secret, Notes). Both the key and the environment-variable form must appear, the key in backticks:

```markdown
| `Kafka:BrokerUrl` | `Kafka__BrokerUrl` | empty; Development: `kafka://localhost:9092` | No | Required. Absolute URL of the message broker. |
```

### Step 8: the drift check

`scripts/ci/check-config-keys.sh` keeps a `keys=( ... )` array and fails when a listed key is missing from the configuration reference. When you add a key to the code, add it to that array (the script's own header says so), under a fitting group comment:

```bash
  # Messaging
  "Kafka:BrokerUrl"
```

The script derives the `Kafka__BrokerUrl` form itself and checks both. Array settings use the `<n>` placeholder, for example `"Kafka:Topics:<n>"`.

## 4. Verify

```bash
# 1. The reference documents the key (exit 0, "0 missing").
bash scripts/ci/check-config-keys.sh

# 2. Build and test.
dotnet build
dotnet test

# 3. Compose still resolves, and the app service carries the mapped variable.
docker compose -f deploy/compose/compose.yaml config | grep -n "Kafka__BrokerUrl"
```

Then prove the validation really runs. Start the app with the value unset and the environment set to something other than `Development` (so `appsettings.Development.json` is not read):

```bash
ASPNETCORE_ENVIRONMENT=Production dotnet run --project src/Host/Acme.Service.Host
```

Startup must stop with exit code 1 and a log line that names `Kafka:BrokerUrl` (INV-17). Then set `Kafka__BrokerUrl=kafka://localhost:9092` and confirm it starts. If it starts without the value, the class is not registered (step 2) or has no validator.

## 5. What breaks if you skip a step

| Skipped | Result |
| --- | --- |
| Options class has no `ValidateOnStart` | The app starts with an empty value and fails later, at first use, with a vague error instead of at startup naming the key. |
| `Register` never called in the host | The options are never bound or validated; code that injects them gets an empty object. |
| `appsettings.Development.json` | Local `dotnet run` fails startup in Development until you set the variable by hand. |
| `.env.example` | New checkouts have no `KAFKA_BROKER_URL`; Compose substitutes an empty string or your default, or fails if you used `:?`. |
| Mapping in `compose.yaml` | The container has a raw `KAFKA_BROKER_URL` that .NET ignores; the app fails startup naming `Kafka:BrokerUrl` even though `.env` has the value. |
| Entry in `render.yaml` and the Render environment | The new instance fails startup with the named key and exits; on Render the previous deploy keeps serving. |
| Secret left in the plain environment or `appsettings` instead of a `sync: false` entry | The credential is committed or visible to anyone who can read the deployment configuration; CI's secret scan (gitleaks) may fail the build. |
| Row in `configuration.md` | Nobody can find the setting; `check-config-keys.sh` fails once the key is in its list. |
| Entry in `check-config-keys.sh` | The reference can silently lose the row later. |

## 6. Common mistakes

- **Single underscore.** `Kafka_BrokerUrl` is the key `Kafka_BrokerUrl`, not `Kafka:BrokerUrl`. The separator is exactly two underscores, and the section and property names keep their casing (matching is case-insensitive, but do not rely on that for readability).
- **Array indexes.** List items are numbered: `Kafka__Topics__0`, `Kafka__Topics__1`. A comma-separated string does not bind to a `List<string>`. See `Auth__Jwt__SigningKeys__0__Kid` in `.env.example` and `Auth__External__Oidc__Scopes__<n>` in the reference. Keep indexes contiguous from 0.
- **The `migrate` and `auth:grant-role` verbs read `DOTNET_ENVIRONMENT`**, not `ASPNETCORE_ENVIRONMENT`, and accept only `--Section:Key=value` overrides on the command line. A setting that exists only in `appsettings.Development.json` is invisible to them unless you set `DOTNET_ENVIRONMENT=Development`. See [configuration](configuration.md#how-configuration-is-loaded).
- **A secret in a committed manifest or `appsettings*.json`.** Both are readable by anyone with repo access. Secrets go to `sync: false` Render environment variables (deployed), `.env` or user secrets (local).
- **A Compose variable that is not mapped into the `app` service's `environment:` block.** Adding `KAFKA_BROKER_URL` to `.env` alone changes nothing; the mapping to `Kafka__BrokerUrl` is the step people forget.
- **Quoting.** In `compose.yaml` and other YAML, quote values that look like numbers or booleans.
- **Editing `.env` and expecting a running container to notice.** Recreate it: `docker compose -f deploy/compose/compose.yaml up -d`.
- **Putting the real value in `deploy/compose/.env.example`.** It is committed; only placeholders and non-secret samples belong there.
