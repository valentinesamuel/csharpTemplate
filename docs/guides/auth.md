# Auth guide

> Applies to: Acme.Service template

How to use the Auth module's endpoints, step by step, with `curl`. The design (fail-closed authorization, token rules, sessions, MFA, external login) is in the [security model](../architecture/security-model.md); the module itself is described in its [README](../../src/Modules/Auth/README.md).

All routes are under `/api/v1/auth`, bodies are JSON in camelCase, and every error is `application/problem+json` with a `code` ([error model](../architecture/error-model.md)). The examples use the local stack on `localhost:8080` and `jq`; the values in angle brackets are placeholders.

## Endpoint summary

| Method and route | Access | Body | Success |
| --- | --- | --- | --- |
| `POST /register` | anonymous, `auth-email` limit | `email`, `password`, `displayName` (optional) | 202, always |
| `POST /confirm-email` | anonymous | `userId`, `token` | 204; 400 `invalid_token` |
| `POST /resend-confirmation` | anonymous, `auth-email` limit | `email` | 202, always |
| `POST /login` | anonymous, `auth-credentials` limit | `email`, `password` | 200 tokens, or 200 `mfaRequired` |
| `POST /login/mfa` | anonymous, `auth-credentials` limit | `mfaToken`, and `code` or `recoveryCode` | 200 tokens |
| `POST /refresh` | anonymous, `auth-credentials` limit | `refreshToken` | 200 tokens; 401 `invalid_refresh_token` |
| `POST /logout` | access token | `refreshToken` | 204, always |
| `POST /logout-all` | access token, live session | none | 204 |
| `GET /me` | access token | none | 200 profile with roles and permissions |
| `POST /change-password` | access token, live session | `currentPassword`, `newPassword` | 204 |
| `POST /forgot-password` | anonymous, `auth-email` limit | `email` | 202, always |
| `POST /reset-password` | anonymous, `auth-credentials` limit | `userId`, `token`, `newPassword` | 204; 400 `invalid_token` |
| `POST /mfa/authenticator` | access token, live session | none | 200 `sharedKey`, `authenticatorUri` |
| `POST /mfa/authenticator/verify` | access token, live session | `code` | 200 `recoveryCodes` (10, shown once) |
| `POST /mfa/disable` | access token, live session | `password` | 204 |
| `POST /mfa/recovery-codes` | access token, live session | `password` | 200 new `recoveryCodes` |
| `GET /roles` | `roles.read`, live session | none | 200 roles with permissions |
| `GET /users/{id}` | `users.read`, live session | none | 200 user |
| `POST /users/{id}/roles/{role}` | `users.manage`, live session | none | 204 |
| `DELETE /users/{id}/roles/{role}` | `users.manage`, live session | none | 204; 409 `last_admin` |
| `POST /users/{id}/lock` | `users.manage`, live session | none | 204 |
| `POST /users/{id}/unlock` | `users.manage`, live session | none | 204 |
| `GET /external/oidc/challenge?returnUrl=` | anonymous, only when OIDC is enabled | none | 302 to the provider |
| `GET /external/oidc/callback` | anonymous, only when OIDC is enabled | none | 302 to `returnUrl` |
| `POST /external/exchange` | anonymous, `auth-credentials` limit, only when OIDC is enabled | `code` | 200 tokens, or 200 `mfaRequired` |

Also anonymous: `GET /.well-known/jwks.json`, the public signing keys (`Cache-Control: public,max-age=300`).

"Live session" means the `live-session` policy: the token's session must still exist and not be revoked in `auth.sessions` (401 `session_revoked` otherwise; 503 `session_store_unavailable` when the Auth database cannot be reached). A rate-limited request gets 429 with `Retry-After`; limits are per client IP (and, for `auth-email`, also per email) and per replica.

## Register, confirm and log in

```bash
BASE=localhost:8080/api/v1/auth

curl -s -XPOST $BASE/register -H 'content-type: application/json' \
  -d '{"email":"user@example.com","password":"correct horse battery staple","displayName":"User"}'
```

Register always answers 202 with a generic message, also for an email that is already registered, so the endpoint cannot be used to discover accounts. A new user gets `Auth:DefaultRole` (`Customer`), and a verification email is queued through the outbox.

The email links to `Auth:Links:ConfirmEmailUrl` with `userId` and `token` query parameters. Your front end reads them, URL-decodes the token and posts them back:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -XPOST $BASE/confirm-email -H 'content-type: application/json' \
  -d '{"userId":"<userId>","token":"<url-decoded token>"}'
# 204

curl -s -XPOST $BASE/resend-confirmation -H 'content-type: application/json' -d '{"email":"user@example.com"}'
# 202, always
```

Log in and keep both tokens:

```bash
curl -s -XPOST $BASE/login -H 'content-type: application/json' \
  -d '{"email":"user@example.com","password":"correct horse battery staple"}' | tee /tmp/tokens.json
ACCESS=$(jq -r .accessToken /tmp/tokens.json)
REFRESH=$(jq -r .refreshToken /tmp/tokens.json)

curl -s $BASE/me -H "authorization: Bearer $ACCESS"
```

A successful login returns `accessToken`, `accessTokenExpiresAt`, `refreshToken`, `refreshTokenExpiresAt` and `tokenType` (`Bearer`). Failures:

- 401 `invalid_credentials` for an unknown email, a wrong password and a locked account alike (5 failures lock the account for 15 minutes);
- 403 `email_not_verified` only after a correct password on an unconfirmed account (when `Auth:Identity:RequireConfirmedEmail` is true).

`GET /me` returns `id`, `email`, `displayName`, `emailConfirmed`, `mfaEnabled`, `roles` and `permissions`.

## Refresh and log out

```bash
curl -s -XPOST $BASE/refresh -H 'content-type: application/json' -d "{\"refreshToken\":\"$REFRESH\"}" | tee /tmp/tokens.json
REFRESH=$(jq -r .refreshToken /tmp/tokens.json)
```

Refresh tokens are single-use. Each refresh returns a new refresh token and a new access token with permissions recomputed from the user's current roles. Presenting an already used refresh token is treated as theft: the whole session is revoked and the call fails with 401 `invalid_refresh_token`, like every other refresh failure. Clients must therefore serialise refresh calls (one in flight per session) and always store the newest refresh token. Refresh tokens travel in the JSON body; the cookie-based variant is described in [security hardening](../operations/security-hardening.md#cookie-based-refresh-tokens).

```bash
# End this session (the refresh token must belong to the caller); always 204
curl -s -o /dev/null -w '%{http_code}\n' -XPOST $BASE/logout -H "authorization: Bearer $ACCESS" \
  -H 'content-type: application/json' -d "{\"refreshToken\":\"$REFRESH\"}"

# End every session of the caller and update the security stamp
curl -s -o /dev/null -w '%{http_code}\n' -XPOST $BASE/logout-all -H "authorization: Bearer $ACCESS"
```

Access tokens are stateless: after a logout they stay valid until they expire (at most `Auth:Jwt:AccessTokenLifetime`, 15 minutes) on endpoints without the live-session policy. Endpoints with the policy reject them at once.

## Passwords

```bash
curl -s -o /dev/null -w '%{http_code}\n' -XPOST $BASE/change-password -H "authorization: Bearer $ACCESS" \
  -H 'content-type: application/json' \
  -d '{"currentPassword":"correct horse battery staple","newPassword":"another long passphrase"}'
# 204: every other session is revoked, the current one stays

curl -s -XPOST $BASE/forgot-password -H 'content-type: application/json' -d '{"email":"user@example.com"}'
# 202, always; an email is sent only to an existing, confirmed account

curl -s -o /dev/null -w '%{http_code}\n' -XPOST $BASE/reset-password -H 'content-type: application/json' \
  -d '{"userId":"<userId>","token":"<url-decoded token>","newPassword":"a brand new passphrase"}'
# 204: every session is revoked and the lockout counter is reset; 400 invalid_token otherwise
```

The reset email links to `Auth:Links:ResetPasswordUrl` with the same `userId` and `token` parameters. Tokens are valid for `Auth:Identity:TokenLifespan` (1 hour) and are generated when the email job runs; they are never stored in the outbox or logged. Passwords need at least `Auth:Identity:Password:RequiredLength` (12) characters and have no composition rules.

## MFA (TOTP)

Enable it with a fresh access token:

```bash
curl -s -XPOST $BASE/mfa/authenticator -H "authorization: Bearer $ACCESS"
# {"sharedKey":"...","authenticatorUri":"otpauth://totp/<Issuer>:<email>?secret=...&issuer=...&digits=6&period=30"}
```

Add the key to an authenticator app (render `authenticatorUri` as a QR code in your client; the API returns no image), then prove it works:

```bash
curl -s -XPOST $BASE/mfa/authenticator/verify -H "authorization: Bearer $ACCESS" \
  -H 'content-type: application/json' -d '{"code":"<6 digits>"}'
# {"recoveryCodes":["...", ...]}  10 codes, shown only this once
```

From now on, login returns an MFA challenge instead of tokens:

```bash
curl -s -XPOST $BASE/login -H 'content-type: application/json' \
  -d '{"email":"user@example.com","password":"another long passphrase"}'
# {"mfaRequired":true,"mfaToken":"..."}

curl -s -XPOST $BASE/login/mfa -H 'content-type: application/json' \
  -d '{"mfaToken":"<mfaToken>","code":"<6 digits>"}'
# or: -d '{"mfaToken":"<mfaToken>","recoveryCode":"<one recovery code>"}'
```

- The MFA token is valid for 5 minutes, allows fewer than 5 attempts and can never be used as a bearer token.
- Failed codes count towards the account lockout.
- A TOTP code (time step) that was already accepted for the user is rejected, so a code cannot be replayed.
- Each recovery code works once. Tokens issued after MFA carry `amr` `["pwd","mfa"]`.

Manage MFA (both require the current password):

```bash
curl -s -XPOST $BASE/mfa/recovery-codes -H "authorization: Bearer $ACCESS" \
  -H 'content-type: application/json' -d '{"password":"<password>"}'      # new codes; old ones stop working

curl -s -o /dev/null -w '%{http_code}\n' -XPOST $BASE/mfa/disable -H "authorization: Bearer $ACCESS" \
  -H 'content-type: application/json' -d '{"password":"<password>"}'      # 204; also resets the authenticator key
```

## Administration

### Bootstrap the first administrator

There is no default admin account and no endpoint that can create one. After the person has registered (and confirmed), grant the role with the `auth:grant-role` verb of the same image, which needs only `ConnectionStrings:Postgres`:

```bash
# Local
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- auth:grant-role --email admin@example.com --role Admin

# Docker Compose
docker compose -f deploy/compose/compose.yaml run --rm migrate auth:grant-role --email admin@example.com --role Admin

# Kubernetes (the image has no shell; this runs the binary directly in a running pod)
kubectl exec -n <namespace> deploy/acme-service -- dotnet Acme.Service.Host.dll auth:grant-role --email admin@example.com --role Admin
```

In Kubernetes, `kubectl exec` runs a second .NET process within the pod's memory limit; on a busy pod prefer a one-off pod or Job ([deployment](../operations/deployment.md#after-the-first-deployment)). Exit codes: 0 when the user has the role afterwards (idempotent), 1 when the user or role does not exist, 2 on invalid usage. The new permissions appear in the next token, so log in again or refresh.

### Admin endpoints

All admin endpoints require the live-session policy and a permission ([permissions](permissions.md)).

```bash
ADMIN=<access token of a user with the Admin role>
curl -s $BASE/roles -H "authorization: Bearer $ADMIN"                       # roles.read
curl -s $BASE/users/<userId> -H "authorization: Bearer $ADMIN"              # users.read
curl -s -o /dev/null -w '%{http_code}\n' -XPOST   $BASE/users/<userId>/roles/Staff -H "authorization: Bearer $ADMIN"
curl -s -o /dev/null -w '%{http_code}\n' -XDELETE $BASE/users/<userId>/roles/Staff -H "authorization: Bearer $ADMIN"
curl -s -o /dev/null -w '%{http_code}\n' -XPOST   $BASE/users/<userId>/lock   -H "authorization: Bearer $ADMIN"
curl -s -o /dev/null -w '%{http_code}\n' -XPOST   $BASE/users/<userId>/unlock -H "authorization: Bearer $ADMIN"
```

- Roles are code-owned (`Admin`, `Staff`, `Customer`); there is no role create or delete endpoint. An unknown role is 404 `role_not_found`, an unknown user 404 `user_not_found`.
- Removing the Admin role from the last administrator is refused with 409 `last_admin`.
- Role changes take effect at the user's next refresh, at most one access-token lifetime later.
- Locking also revokes all of the user's sessions, so refresh fails at once; outstanding access tokens still work on endpoints without the live-session policy until they expire. For an urgent cut-off, lock the user and keep the access-token lifetime short.

## External OIDC login

Off by default. When `Auth:External:Oidc:Enabled` is false the three external routes are not mapped: an anonymous request to them gets 401 (the fallback policy answers unmatched routes) and an authenticated one 404.

### Enable it

1. Register a confidential client at your provider (authorization code flow) with the redirect URI `https://<your host>/api/v1/auth/external/oidc/signin`. That path is handled by the OIDC middleware, not by an endpoint.
2. Configure ([configuration](configuration.md#external-oidc-login)), keeping the client secret in a secret store:

   ```bash
   Auth__External__Oidc__Enabled=true
   Auth__External__Oidc__DisplayName=Corporate SSO
   Auth__External__Oidc__Authority=https://<provider>/
   Auth__External__Oidc__ClientId=<client id>
   Auth__External__Oidc__ClientSecret=<client secret>
   Auth__External__Oidc__AllowedReturnUrls__0=https://app.example.com/auth/callback
   ```

3. Restart. With missing or invalid settings the service refuses to start and names the key. The authority must be https outside Development.
4. Serve the API over HTTPS: the correlation and external cookies are needed across the provider's redirect.

### The flow

```text
browser -> GET /api/v1/auth/external/oidc/challenge?returnUrl=https://app.example.com/auth/callback
        -> 302 to the provider (code flow with PKCE)
provider -> GET /api/v1/auth/external/oidc/signin        (OIDC middleware; sets a 5-minute external cookie)
         -> GET /api/v1/auth/external/oidc/callback      (finds or provisions the user)
         -> 302 https://app.example.com/auth/callback?code=<one-time code>
client  -> POST /api/v1/auth/external/exchange {"code":"<one-time code>"} -> tokens, or mfaRequired
```

```bash
curl -s -XPOST $BASE/external/exchange -H 'content-type: application/json' -d '{"code":"<one-time code>"}'
```

- `returnUrl` must exactly equal an `AllowedReturnUrls` entry, otherwise 400 `invalid_return_url`.
- A first login is provisioned only when no local account uses that email; the email counts as confirmed only when the provider says `email_verified=true`. An existing local account is never linked automatically: the client gets `returnUrl?error=account_exists`. Provider failures redirect with `error=external_login_failed`.
- The one-time code is 32 random bytes, stored hashed, valid for 60 seconds and single-use (401 `invalid_exchange_code` otherwise). No token ever appears in a URL.
- When the provider is down only external login fails (the challenge answers 503 `external_provider_unavailable`); password login is unaffected.

Known limitations: tokens from an external login followed by MFA carry `amr` `["pwd","mfa"]` rather than `["ext","mfa"]`, and two simultaneous first logins with the same email can make one of them fail with 500.

## Client guidance

- Store tokens outside the reach of page scripts where possible; a backend-for-frontend that keeps the refresh token server-side is the recommended browser setup.
- Refresh shortly before `accessTokenExpiresAt`, one call at a time per session, and replace the stored refresh token with every response.
- Treat 401 `invalid_refresh_token` as "sign in again".
- On 503 with `Retry-After` (`session_store_unavailable`), retry later; do not discard the tokens.
- Permission changes reach a client at its next refresh; tell users to sign in again after a role change if it must apply at once.
