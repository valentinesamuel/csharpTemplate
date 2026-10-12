# JWT signing-key rotation

> Applies to: Acme.Service template

Access tokens (and the short-lived MFA tokens) are RS256 JWTs signed by the single `Active` key of the key ring `Auth:Jwt:SigningKeys`. Tokens are validated with every key in the ring (`Active`, `Next` and `Retiring`), and `GET /.well-known/jwks.json` publishes the public part of all of them with `Cache-Control: public,max-age=300` ([ADR-0006](../adr/0006-rs256-jwt-key-ring-and-rotation.md), [security model](../architecture/security-model.md#signing-keys)).

Refresh tokens are opaque random values stored as hashes; they do not depend on the signing key, so a rotation never logs anyone out.

## Facts the runbook relies on

- Keys are loaded **at startup only** (R-29). A running process never reloads the ring, but every process that starts after a change does: the rolling restart in each step, and also any instance your platform adds, reschedules or restarts after a crash. So the service's environment must hold every `PrivateKeyBase64` that the `Kid`/`Status` values need **before** those values are changed, and a changed ring is in effect for new instances as soon as it is applied.
- Startup fails unless exactly one key is `Active`, every `Kid` is unique, and every key is RSA with at least 2048 bits; each key has exactly one of `PrivateKeyPath` or `PrivateKeyBase64`. On Render the key is an environment variable, `PrivateKeyBase64` (platforms that mount key files can use `PrivateKeyPath` instead); a `PrivateKeyPath` that points at a missing file, or a configured key whose `PrivateKeyBase64` is missing, also fails startup.
- A token whose `kid` is not in an instance's ring is rejected by that instance (401).
- Lifetimes: access token `Auth:Jwt:AccessTokenLifetime` (15 minutes), clock skew `Auth:Jwt:ClockSkew` (30 seconds), MFA token 5 minutes, JWKS `Cache-Control: max-age=300` (5 minutes).
- **External validators** (an API gateway or another service validating these tokens from the JWKS document) follow their own policy, not this service's. Many ignore `Cache-Control`. Microsoft.IdentityModel, for example, refreshes its metadata automatically every 12 hours, or when it meets an unknown `kid` (at most once every 5 minutes), and defaults to a 5-minute clock skew. Find out the refresh interval and clock skew of every external validator before you start.

## Generate the new key

Generate it on a trusted machine, outside the repository, and never commit it (INV-11):

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out signing-key-1.pem
chmod 600 signing-key-1.pem
base64 < signing-key-1.pem | tr -d '\n' > signing-key-1.b64     # the value you paste into the Render environment
```

Pick a new, unique `Kid` (1 to 64 characters of `A-Z a-z 0-9 . _ -`), for example `signing-key-1` or a date such as `2026-11`.

## Before you start: one revision, one place

The key ring is plain environment variables of the Render service: the private keys (`PrivateKeyBase64`) and the key ids (`Kid`) are `sync: false` values edited in the dashboard, and the `Status` values are set in `render.yaml`. **Each step below is a change to those variables.** Edit a Blueprint-managed value in `render.yaml` (a dashboard edit of it can be overwritten the next time the Blueprint syncs), and edit `sync: false` values in the dashboard. Save all the variables of a step in one change so the service redeploys once with a consistent ring.

- Do not deploy a revision that predates the rotation until it is finished. A `render.yaml` from before step 4 still references `signing-key-0`'s entries; applied after that key was removed from the dashboard, it makes every new instance fail startup.
- Every redeploy also runs the start-up migration (`Migrations__RunOnStart=true`) for the revision being deployed. Do not combine a rotation step with a release, and never run two rotations at once.

## The four-step runbook

The example rotates from `signing-key-0` (index 0, currently `Active`) to `signing-key-1`, where the private keys are `Auth__Jwt__SigningKeys__<n>__PrivateKeyBase64` environment variables on the Render service and the key entries (`Kid`, `Status`) are environment variables beside them. There is no key volume to mount.

### Step 1: publish the new key as `Next`

Add the new key to the service's environment, keeping the existing one: create `Auth__Jwt__SigningKeys__1__PrivateKeyBase64` with the contents of `signing-key-1.b64` (declare it as `sync: false` in `render.yaml` too, so the Blueprint tracks it). Check that both `...__0__PrivateKeyBase64` and `...__1__PrivateKeyBase64` are present, then add the entries:

```yaml
  Auth__Jwt__SigningKeys__1__Kid: signing-key-1
  Auth__Jwt__SigningKeys__1__Status: Next
```

Save the change and let Render redeploy (or trigger a manual deploy), then wait until all instances are healthy:

```bash
curl -s https://<host>/.well-known/jwks.json | jq -r '.keys[].kid'      # both kids
```

Wait until **every** replica runs with the new ring (the rollout has completed and no old instance is left). Then wait until every external validator has fetched the new JWKS: at least 5 minutes (`max-age`) for validators that honour it, otherwise their real refresh interval (up to 12 hours for Microsoft.IdentityModel's automatic refresh), or force a metadata refresh on them.

### Step 2: promote the new key, demote the old one

Change the values:

```yaml
  Auth__Jwt__SigningKeys__0__Status: Retiring
  Auth__Jwt__SigningKeys__1__Status: Active
```

Save and redeploy as in step 1. New tokens are now signed with `signing-key-1`; tokens signed with `signing-key-0` keep validating because it is `Retiring`.

### Step 3: wait for old tokens to expire

Wait at least the access-token lifetime plus the **largest** clock skew of any validator, counted from the moment the **last** instance with the old configuration stopped: 15 minutes 30 seconds when only this service validates its tokens, 20 minutes when external validators use the .NET default skew of 5 minutes. No token signed with `signing-key-0` is accepted anywhere after that.

### Step 4: remove the old key

Remove the index-0 entries and move the remaining key to index 0, so the ring has no gaps:

```yaml
  Auth__Jwt__SigningKeys__0__Kid: signing-key-1
  Auth__Jwt__SigningKeys__0__Status: Active
```

Make this a **single save**, because an instance that starts between two separate edits would pair the new `Kid` with the old key material. In one change: set `Auth__Jwt__SigningKeys__0__PrivateKeyBase64` to the new key (the contents of `signing-key-1.b64`), set the index-0 `Kid` and `Status` as above, and delete the index-1 `Kid`, `Status` and `PrivateKeyBase64` entries (also remove them from `render.yaml`). The old key is already unused, since step 3 waited for its tokens to expire. After the rollout has completed, destroy every copy of the old private key. The index has no meaning; the `kid` identifies the key.

Environment values are read at start, so the redeploy in each step loads the new ring everywhere. Instances that start for any other reason between the change and the redeploy already load the new ring, which is safe because every step is compatible with the previous one.

## Rolling-deployment caveat

During each rollout, old and new instances serve at the same time with different rings. The order above keeps that safe:

- After step 1 every instance can **validate** the new key before any instance **signs** with it. If step 2 started while some instances still ran the step-0 ring, a token signed by a new instance would be rejected (401) by an old instance that does not know `signing-key-1`.
- During step 2, old instances still sign with `signing-key-0` and new instances with `signing-key-1`; both are in every instance's ring, so both validate everywhere.
- Never combine steps 1 and 2 in one deployment.

The same applies to anything else that validates the service's tokens from the JWKS document: it must have fetched the new key before step 2, according to its own refresh policy (see the facts above).

## Local and Compose

The key ring comes from `deploy/compose/.env` (written by `dotnet run scripts/dev-env.cs`) or from user secrets, in the `PrivateKeyBase64` form:

```bash
Auth__Jwt__SigningKeys__1__Kid=<new kid>
Auth__Jwt__SigningKeys__1__Status=Next
Auth__Jwt__SigningKeys__1__PrivateKeyBase64=<contents of signing-key-1.b64>
```

Recreate the app after each change with `docker compose -f deploy/compose/compose.yaml up -d app`. Locally it is usually simpler to regenerate everything with `dotnet run scripts/dev-env.cs -- --force` and log in again.

## Emergency rotation (key compromised)

If the private key may have leaked, do not wait: deploy a ring in which the new key is `Active` and the compromised key is absent, in one rollout (still from the current revision, with the new key already added to the environment).

- This service stops accepting tokens signed with the old key (401). Clients recover by refreshing: refresh tokens do not depend on the signing key, so the refresh succeeds and returns a token signed with the new key.
- During the rollout, tokens issued by not-yet-replaced instances are rejected by the new ones; expect a short burst of 401s.
- **External validators keep trusting the compromised key until their cached JWKS is refreshed** (up to their refresh interval, 12 hours for Microsoft.IdentityModel's automatic refresh). Force a metadata refresh or restart them, or block the old `kid` there, as part of the incident.
- Also consider `logout-all` or locking accounts if the attacker may have minted tokens for specific users, and review the `SecurityEvent` logs.

## Verify

```bash
curl -s https://<host>/.well-known/jwks.json | jq -r '.keys[].kid'

# kid of a token: decode its header (base64url without padding)
s=$(echo "<access token>" | cut -d. -f1 | tr '_-' '/+')
while [ $(( ${#s} % 4 )) -ne 0 ]; do s="$s="; done
echo "$s" | base64 -d; echo
```

The header of a newly issued token shows `"kid":"signing-key-1"` after step 2.
