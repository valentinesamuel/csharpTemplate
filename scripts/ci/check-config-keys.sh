#!/usr/bin/env bash
# Configuration reference drift check (plan P25). Fails when a configuration key is missing from
# docs/guides/configuration.md, in either its ':' form (Auth:Jwt:Issuer) or its environment-variable form
# (Auth__Jwt__Issuer).
#
# The key list below is derived from the plan's configuration reference (§0.7) and kept here, because the plan does
# not ship with an initialised service. Array entries use the placeholder <n>, and the Serilog:* wildcard is
# represented by the two keys the template sets. Keys the code reads beyond §0.7 are listed too, so the reference
# stays complete; when you add a configuration key to the code, add it here and to the guide.
#
# Usage: bash scripts/ci/check-config-keys.sh [path-to-configuration.md]
#   Exit 0 when every key is documented, 1 when any is missing, 2 on bad usage.
set -euo pipefail

repo_root="${CONFIG_KEYS_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
doc="${1:-${repo_root}/docs/guides/configuration.md}"

if [[ ! -f "${doc}" ]]; then
  echo "error: ${doc} does not exist" >&2
  exit 2
fi

keys=(
  # Connection strings
  "ConnectionStrings:Postgres"
  "ConnectionStrings:PostgresMigrations"

  # Providers
  "Cache:Provider"
  "Cache:Redis:ConnectionString"
  "Cache:DefaultTtl"
  "Blob:Provider"
  "Blob:Local:RootPath"
  "Blob:S3:ServiceUrl"
  "Blob:S3:Region"
  "Blob:S3:Bucket"
  "Blob:S3:AccessKey"
  "Blob:S3:SecretKey"
  "Blob:S3:ForcePathStyle"
  "Blob:AzureBlob:ConnectionString"
  "Blob:AzureBlob:ServiceUri"
  "Blob:AzureBlob:Container"
  "Notifications:Provider"
  "Notifications:Smtp:Host"
  "Notifications:Smtp:Port"
  "Notifications:Smtp:UseStartTls"
  "Notifications:Smtp:Username"
  "Notifications:Smtp:Password"
  "Notifications:Smtp:From"
  "Jobs:Provider"
  "Jobs:Hangfire:Schema"
  "Jobs:Hangfire:WorkerCount"
  "Jobs:Hangfire:DashboardEnabled"
  "Locks:Provider"

  # Messaging and idempotency
  "Outbox:PollingInterval"
  "Outbox:BatchSize"
  "Outbox:MaxAttempts"
  "Outbox:RetentionDays"
  "Inbox:RetentionDays"
  "Idempotency:Ttl"
  "Idempotency:InProgressTtl"
  "Idempotency:FailMode"

  # Auth
  "Auth:Jwt:Issuer"
  "Auth:Jwt:Audience"
  "Auth:Jwt:AccessTokenLifetime"
  "Auth:Jwt:RefreshTokenLifetime"
  "Auth:Jwt:SessionAbsoluteLifetime"
  "Auth:Jwt:ClockSkew"
  "Auth:Jwt:SigningKeys:<n>:Kid"
  "Auth:Jwt:SigningKeys:<n>:Status"
  "Auth:Jwt:SigningKeys:<n>:PrivateKeyPath"
  "Auth:Jwt:SigningKeys:<n>:PrivateKeyBase64"
  "Auth:Identity:RequireConfirmedEmail"
  "Auth:Identity:Password:RequiredLength"
  "Auth:Identity:Lockout:MaxFailedAccessAttempts"
  "Auth:Identity:Lockout:DefaultLockoutTimeSpan"
  "Auth:Identity:TokenLifespan"
  "Auth:RateLimiting:Credentials:PermitLimit"
  "Auth:RateLimiting:Credentials:Window"
  "Auth:RateLimiting:Email:PermitLimit"
  "Auth:RateLimiting:Email:Window"
  "Auth:Links:ConfirmEmailUrl"
  "Auth:Links:ResetPasswordUrl"
  "Auth:DefaultRole"
  "Auth:Mfa:Issuer"
  "Auth:External:Oidc:Enabled"
  "Auth:External:Oidc:DisplayName"
  "Auth:External:Oidc:Authority"
  "Auth:External:Oidc:ClientId"
  "Auth:External:Oidc:ClientSecret"
  "Auth:External:Oidc:Scopes"
  "Auth:External:Oidc:AllowedReturnUrls"

  # HTTP, observability, OpenAPI
  "Http:ForwardedHeaders:KnownNetworks"
  "Http:ForwardedHeaders:KnownProxies"
  "Observability:ServiceName"
  "Observability:Otlp:Endpoint"
  "Serilog:MinimumLevel:Default"
  "Serilog:MinimumLevel:Override:<category>"
  "OpenApi:Enabled"

  # Read by the code, beyond §0.7
  "Cache:Redis:ConnectTimeout"
  "Cache:Redis:SyncTimeout"
  "Cache:Redis:MaxValueBytes"
  "Blob:S3:CreateIfMissing"
  "Blob:AzureBlob:CreateIfMissing"
  # <sample:orders>
  "Orders:PendingExpiry"
  "Orders:Attachments:MaxBytes"
  # </sample:orders>
)

missing=0
for key in "${keys[@]}"; do
  env_form="${key//:/__}"
  if ! grep -qF -- "\`${key}\`" "${doc}"; then
    echo "MISSING ${key}"
    missing=$((missing + 1))
  fi
  if ! grep -qF -- "${env_form}" "${doc}"; then
    echo "MISSING ${env_form} (environment-variable form of ${key})"
    missing=$((missing + 1))
  fi
done

echo "check-config-keys: ${#keys[@]} key(s) checked against ${doc#"${repo_root}/"}, ${missing} missing"
[[ ${missing} -eq 0 ]]
