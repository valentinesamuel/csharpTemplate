# JWT signing-key rotation

> Applies to: Acme.Service template

Access tokens (and the short-lived MFA tokens) are RS256 JWTs signed by the single `Active` key of the key ring `Auth:Jwt:SigningKeys`. Tokens are validated with every key in the ring (`Active`, `Next` and `Retiring`), and `GET /.well-known/jwks.json` publishes the public part of all of them with `Cache-Control: public,max-age=300` ([ADR-0006](../adr/0006-rs256-jwt-key-ring-and-rotation.md), [security model](../architecture/security-model.md#signing-keys)).

Refresh tokens are opaque random values stored as hashes; they do not depend on the signing key, so a rotation never logs anyone out.

## Facts the runbook relies on

- Keys are loaded **at startup only** (R-29). A running process never reloads the ring, but every process that starts after a change does: the rolling restart in each step, and also any pod the HPA adds, a rescheduled pod or a crash restart. So the Secret must hold every file the ConfigMap references **before** the ConfigMap change is applied, and a ring stays in effect for new pods as soon as it is applied, even before you restart.
- Startup fails unless exactly one key is `Active`, every `Kid` is unique, and every key is RSA with at least 2048 bits; each key has exactly one of `PrivateKeyPath` or `PrivateKeyBase64`. A `PrivateKeyPath` that points at a missing file also fails startup.
- A token whose `kid` is not in a pod's ring is rejected by that pod (401).
- Lifetimes: access token `Auth:Jwt:AccessTokenLifetime` (15 minutes), clock skew `Auth:Jwt:ClockSkew` (30 seconds), MFA token 5 minutes, JWKS `Cache-Control: max-age=300` (5 minutes).
- **External validators** (an API gateway or another service validating these tokens from the JWKS document) follow their own policy, not this service's. Many ignore `Cache-Control`. Microsoft.IdentityModel, for example, refreshes its metadata automatically every 12 hours, or when it meets an unknown `kid` (at most once every 5 minutes), and defaults to a 5-minute clock skew. Find out the refresh interval and clock skew of every external validator before you start.

## Generate the new key

Generate it on a trusted machine, outside the repository, and never commit it (INV-11):

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out signing-key-1.pem
chmod 600 signing-key-1.pem
base64 < signing-key-1.pem | tr -d '\n' > signing-key-1.b64     # only for the PrivateKeyBase64 form
```

Pick a new, unique `Kid` (1 to 64 characters of `A-Z a-z 0-9 . _ -`), for example `signing-key-1` or a date such as `2026-11`.

## Before you start: one revision, one branch

The key entries live in the overlay's `config-patch.yaml`, so **each configuration step below is a change in git**. Commit each step on the branch you deploy from and apply only from that branch's current revision:

- Do not deploy from any other revision until the rotation is finished. An overlay from before step 4 still references `signing-key-0.pem`; applied after the file was removed from the Secret, it makes every new pod fail startup.
- `kubectl apply -k deploy/k8s/overlays/<env>` applies the whole overlay, including its image tag, and does **not** run the migration Job. Make sure the overlay's image tag is the one currently running, so a rotation step never rolls out an unreleased image without its migrations.
- Never run two rotations at once, and do not combine a rotation step with a release.

## The four-step runbook

The example rotates from `signing-key-0` (index 0, currently `Active`) to `signing-key-1` in Kubernetes, where key files are mounted from the Secret `acme-service-jwt-keys` at `/etc/acme-service/jwt` and the key entries live in `deploy/k8s/overlays/<env>/config-patch.yaml`.

### Step 1: publish the new key as `Next`

First add the new file to the Secret, keeping the existing one:

- If the Secret is managed by External Secrets, Sealed Secrets, Vault or similar, add `signing-key-1.pem` in the secret store or the sealed manifest and let the controller sync it; do not edit the Kubernetes Secret by hand (the controller would revert it).
- For a plain Secret, add only the new key:

  ```bash
  kubectl patch secret acme-service-jwt-keys -n <ns> --type=merge \
    -p "{\"data\":{\"signing-key-1.pem\":\"$(base64 < signing-key-1.pem | tr -d '\n')\"}}"
  ```

Check that the key is there (`kubectl get secret acme-service-jwt-keys -n <ns> -o jsonpath='{.data}' | jq 'keys'`), then add the entry to `config-patch.yaml` and commit:

```yaml
  Auth__Jwt__SigningKeys__1__Kid: signing-key-1
  Auth__Jwt__SigningKeys__1__Status: Next
  Auth__Jwt__SigningKeys__1__PrivateKeyPath: /etc/acme-service/jwt/signing-key-1.pem
```

Apply and restart, then wait:

```bash
kubectl apply -k deploy/k8s/overlays/<env>
kubectl rollout restart deployment/acme-service -n <ns>
kubectl rollout status deployment/acme-service -n <ns>
curl -s https://<host>/.well-known/jwks.json | jq -r '.keys[].kid'      # both kids
```

Wait until **every** replica runs with the new ring (the rollout has completed and no old pod is left). Then wait until every external validator has fetched the new JWKS: at least 5 minutes (`max-age`) for validators that honour it, otherwise their real refresh interval (up to 12 hours for Microsoft.IdentityModel's automatic refresh), or force a metadata refresh on them.

### Step 2: promote the new key, demote the old one

Commit:

```yaml
  Auth__Jwt__SigningKeys__0__Status: Retiring
  Auth__Jwt__SigningKeys__1__Status: Active
```

Apply and roll out as in step 1. New tokens are now signed with `signing-key-1`; tokens signed with `signing-key-0` keep validating because it is `Retiring`.

### Step 3: wait for old tokens to expire

Wait at least the access-token lifetime plus the **largest** clock skew of any validator, counted from the moment the **last** pod with the old configuration stopped: 15 minutes 30 seconds when only this service validates its tokens, 20 minutes when external validators use the .NET default skew of 5 minutes. No token signed with `signing-key-0` is accepted anywhere after that.

### Step 4: remove the old key

Remove the index-0 entries and move the remaining key to index 0, so the ring has no gaps, and commit:

```yaml
  Auth__Jwt__SigningKeys__0__Kid: signing-key-1
  Auth__Jwt__SigningKeys__0__Status: Active
  Auth__Jwt__SigningKeys__0__PrivateKeyPath: /etc/acme-service/jwt/signing-key-1.pem
```

Delete the index-1 entries, apply and roll out. Only after the rollout has completed, remove the old file from the Secret (in the secret store, or for a plain Secret with `kubectl patch secret acme-service-jwt-keys -n <ns> --type=json -p '[{"op":"remove","path":"/data/signing-key-0.pem"}]'`) and destroy every copy of the old private key. The index has no meaning; the `kid` identifies the key.

Because `acme-service-config` is a plain ConfigMap, `kubectl apply` alone does not restart the running pods; the `rollout restart` in each step loads the new ring everywhere at once. Pods that start for any other reason between the apply and the restart already load the new ring, which is safe because every step is compatible with the previous one.

## Rolling-deployment caveat

During each rollout, old and new pods serve at the same time with different rings. The order above keeps that safe:

- After step 1 every pod can **validate** the new key before any pod **signs** with it. If step 2 started while some pods still ran the step-0 ring, a token signed by a new pod would be rejected (401) by an old pod that does not know `signing-key-1`.
- During step 2, old pods still sign with `signing-key-0` and new pods with `signing-key-1`; both are in every pod's ring, so both validate everywhere.
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

If the private key may have leaked, do not wait: deploy a ring in which the new key is `Active` and the compromised key is absent, in one rollout (still from the deploy branch, with the Secret updated first).

- This service stops accepting tokens signed with the old key (401). Clients recover by refreshing: refresh tokens do not depend on the signing key, so the refresh succeeds and returns a token signed with the new key.
- During the rollout, tokens issued by not-yet-replaced pods are rejected by the new ones; expect a short burst of 401s.
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
