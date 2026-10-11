# Kubernetes manifests (Kustomize)

```
deploy/k8s/
  base/                     web app: ServiceAccount, ConfigMap, Deployment, Service, PDB, HPA
  overlays/{dev,qa,prod}/   namespace, image tag, replicas, config, resources (+ prod topology spread)
  migration/base/           one-shot Job `acme-service-migrate` running the image with `migrate`
  migration/overlays/{dev,qa,prod}/
```

| Env  | Namespace          | Replicas (HPA min-max) |
|------|--------------------|------------------------|
| dev  | `acme-service-dev`  | 1 (1-2)  |
| qa   | `acme-service-qa`   | 2 (2-4)  |
| prod | `acme-service-prod` | 3 (3-10), spread across zones and nodes |

The image tag is set in each overlay's `images:` block (`name: acme-service`). Keep the app overlay and the
migration overlay of an environment on the **same** tag; CI normally does this with
`kustomize edit set image acme-service=<registry>/acme-service:<tag>` in both directories.

## Apply order

Migrations never run in the web app (INV-16). Every deploy runs the migration Job first and only rolls the app when it
succeeded:

```sh
kubectl delete job acme-service-migrate -n <ns> --ignore-not-found
kubectl apply -k deploy/k8s/migration/overlays/<env>
kubectl wait --for=condition=complete job/acme-service-migrate -n <ns> --timeout=10m
kubectl apply -k deploy/k8s/overlays/<env>
```

- Jobs are immutable, hence the delete first. A finished Job is also garbage-collected after 1 hour
  (`ttlSecondsAfterFinished: 3600`).
- If the Job fails (`backoffLimit: 1`), `kubectl wait` times out and the deploy stops; the old pods keep serving.
  Inspect it with `kubectl logs job/acme-service-migrate -n <ns>`.
- During a rolling update new pods only become ready once `/health/ready` passes (it fails while migrations are pending)
  and `maxUnavailable: 0` keeps the old pods serving until then.
- The migration Job generates its own ConfigMap (`acme-service-migrate-config-<hash>`), so it does not depend on (or
  overwrite) the app's `acme-service-config`.
- `acme-service-config` is a plain ConfigMap: changing only configuration does not roll the pods. Run
  `kubectl rollout restart deployment/acme-service -n <ns>` after a config-only change.

## Secrets (created out of band, never committed)

No `Secret` object exists anywhere in this repository (INV-11; `scripts/ci/validate-k8s.sh` fails if one is rendered).
Create them per namespace with your secret store (External Secrets Operator, Sealed Secrets, Vault, CSI driver) or,
for a quick start, `kubectl`. The values below are placeholders; never paste real values into shell history on a shared
machine — prefer `--from-file` or `--from-env-file` with files outside the repository.

| Secret | Keys | Used by |
|--------|------|---------|
| `acme-service-secrets` | `ConnectionStrings__Postgres`, `Cache__Redis__ConnectionString`, `Blob__S3__AccessKey`, `Blob__S3__SecretKey` (or `Blob__AzureBlob__ConnectionString`), `Notifications__Smtp__Username`, `Notifications__Smtp__Password`, optional `Auth__External__Oidc__ClientSecret` | app (`envFrom`), migration Job (`ConnectionStrings__Postgres` only) |
| `acme-service-jwt-keys` | `signing-key-0.pem` (RSA-3072 PKCS#8 PEM private key) | app, mounted read-only at `/etc/acme-service/jwt` |
| `acme-service-migrations-secrets` | `ConnectionStrings__PostgresMigrations` (role allowed to run DDL) | migration Job only |

```sh
NS=acme-service-dev

kubectl create secret generic acme-service-secrets -n "$NS" \
  --from-literal=ConnectionStrings__Postgres='Host=<host>;Port=5432;Database=acme_service;Username=<app-user>;Password=<password>;SSL Mode=Require' \
  --from-literal=Cache__Redis__ConnectionString='<host>:6379,password=<password>,ssl=true' \
  --from-literal=Blob__S3__AccessKey='<access-key>' \
  --from-literal=Blob__S3__SecretKey='<secret-key>' \
  --from-literal=Notifications__Smtp__Username='<user>' \
  --from-literal=Notifications__Smtp__Password='<password>'

kubectl create secret generic acme-service-jwt-keys -n "$NS" \
  --from-file=signing-key-0.pem=<path-outside-repo>/signing-key-0.pem

kubectl create secret generic acme-service-migrations-secrets -n "$NS" \
  --from-literal=ConnectionStrings__PostgresMigrations='Host=<host>;Port=5432;Database=acme_service;Username=<migrator-user>;Password=<password>;SSL Mode=Require'
```

A missing Secret or key keeps the pod from starting (`CreateContainerConfigError`), and incomplete configuration fails
startup through options validation (INV-17), never silently.

JWT key rotation: add `signing-key-1.pem` to `acme-service-jwt-keys` and the matching
`Auth__Jwt__SigningKeys__1__Kid/__Status/__PrivateKeyPath` entries to the overlay's `config-patch.yaml`, then demote
key 0 once its tokens have expired.

## Configuration

Non-secret settings live in `base/configmap.yaml` and the overlay `config-patch.yaml` files (providers, S3
region/bucket, SMTP host, email link URLs, `ASPNETCORE_ENVIRONMENT=Production`). The bucket names, SMTP host, registry
and `*.example.com` URLs are placeholders to replace per installation. Do not use `Blob__Provider=Local`: the root
filesystem is read-only and D-21 rejects dev-only providers in Production. To use Azure Blob instead of S3, set
`Blob__Provider: AzureBlob` plus `Blob__AzureBlob__Container` (and `ServiceUri` for workload identity, or a
`Blob__AzureBlob__ConnectionString` key in the Secret).

## Expand/contract migrations

Old and new pods run side by side during a rolling update, and the migration runs before the new pods exist, so every
migration must be compatible with the **previous** release:

1. **Expand** (release N): add new tables/columns as nullable or with defaults, add new indexes concurrently where
   possible, write to both old and new shapes. Never rename or drop in the same release that stops using something.
2. **Migrate data**: backfill in the migration or a background job, idempotently.
3. **Contract** (release N+1 or later, once no running version reads the old shape): drop the old columns/tables.

Rollback means redeploying the previous image without running a down migration; that is safe only because of the rule
above. Destructive changes (drops, type narrowing, NOT NULL on existing columns) belong in a contract release only.

## Validation

```sh
bash scripts/ci/validate-k8s.sh     # kustomize build | kubeconform -strict, and no Secret objects
```
