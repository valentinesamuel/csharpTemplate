# Deployment

> Applies to: Acme.Service template

The service ships as one container image (`Dockerfile`) that runs either the web app or, with the `migrate` argument, the schema migration. Kubernetes manifests are Kustomize bases and overlays under `deploy/k8s/`; their reference is `deploy/k8s/README.md`. Docker Compose (`deploy/compose/compose.yaml`) is for local work only.

## What you need first

1. **An image in your registry.** CI builds and scans the image but does not push it. Build with `docker build -t <registry>/acme-service:<tag> .` from the repository root (or add a push job; `.github/workflows/ci.yml` contains a commented example), and set the same tag in both overlays of the environment:

   ```bash
   (cd deploy/k8s/overlays/<env> && kustomize edit set image acme-service=<registry>/acme-service:<tag>)
   (cd deploy/k8s/migration/overlays/<env> && kustomize edit set image acme-service=<registry>/acme-service:<tag>)
   ```

2. **The three Secrets** in the target namespace, created outside the repository: `acme-service-secrets`, `acme-service-jwt-keys` and `acme-service-migrations-secrets` ([secrets](secrets.md)).
3. **Non-secret configuration** in the overlay's `config-patch.yaml`: providers, S3 bucket and region (or Azure container), SMTP host and sender, the email link URLs (`Auth__Links__ConfirmEmailUrl`, `Auth__Links__ResetPasswordUrl`), `Http__ForwardedHeaders__KnownNetworks__0` for your ingress network and, if wanted, `Observability__Otlp__Endpoint`. The placeholders (`*.example.com`, bucket names, registry) must be replaced ([configuration](../guides/configuration.md)).
4. **PostgreSQL** reachable from the cluster, with the database created, a migrator role and an application role ([security hardening](security-hardening.md#per-schema-database-roles)). Redis is needed when `Cache__Provider=Redis` (the overlays' default).

| Environment | Namespace | Replicas (HPA min to max) |
| --- | --- | --- |
| dev | `acme-service-dev` | 1 (1 to 2) |
| qa | `acme-service-qa` | 2 (2 to 4) |
| prod | `acme-service-prod` | 3 (3 to 10), spread across zones and nodes |

## Apply order

Migrations never run in the web app (INV-16). Every deployment runs the migration Job first and rolls the app only when the Job succeeded:

```bash
NS=<ns>

# 1. Never delete a migration Job that is still running: deleting it kills an in-flight migration.
if [ -n "$(kubectl get job acme-service-migrate -n "$NS" -o jsonpath='{.status.active}' --ignore-not-found)" ]; then
  echo "acme-service-migrate is still active; wait for it to finish" >&2; exit 1
fi
kubectl delete job acme-service-migrate -n "$NS" --ignore-not-found

# 2. Run the migration and wait until it completes OR fails (activeDeadlineSeconds is 900 s, so allow 16 minutes).
kubectl apply -k deploy/k8s/migration/overlays/<env>
deadline=$((SECONDS + 960))
while :; do
  complete=$(kubectl get job acme-service-migrate -n "$NS" -o jsonpath='{.status.conditions[?(@.type=="Complete")].status}')
  failed=$(kubectl get job acme-service-migrate -n "$NS" -o jsonpath='{.status.conditions[?(@.type=="Failed")].status}')
  [ "$complete" = "True" ] && break
  if [ "$failed" = "True" ] || [ "$SECONDS" -ge "$deadline" ]; then
    kubectl logs job/acme-service-migrate -n "$NS"; echo "migration failed or timed out" >&2; exit 1
  fi
  sleep 5
done

# 3. Roll the app.
kubectl apply -k deploy/k8s/overlays/<env>
kubectl rollout status deployment/acme-service -n "$NS" --timeout=10m
```

The short form in `deploy/k8s/README.md` (`kubectl wait --for=condition=complete ... --timeout=10m`) works when the Job succeeds, but on a failed Job it waits for the whole timeout, and 10 minutes is shorter than the Job's own 15-minute deadline. Use at least `--timeout=16m`, or the loop above, which stops as soon as the Job fails.

- Jobs are immutable, so the previous run is deleted first, but only once it is no longer active (`.status.active` empty). A finished Job is also removed automatically after one hour.
- The Job runs the same image with `args: ["migrate"]`: every module's EF migrations in registration order under one PostgreSQL advisory lock, then the Hangfire schema (the migration overlays set `Jobs__Provider=Hangfire`), then the Auth permission and role seeder. It is idempotent. Everything must finish within `activeDeadlineSeconds: 900`; see [migrations](migrations.md#time-limit).
- If the Job fails (`backoffLimit: 1`: two pod attempts) the deployment stops before the app is touched; the old pods keep serving. Read `kubectl logs job/acme-service-migrate -n <ns>`.
- During the rolling update (`maxSurge: 1`, `maxUnavailable: 0`) a new pod only receives traffic once `/health/ready` passes. Readiness fails while any module has pending migrations, so a pod started before the Job finished waits instead of serving.
- `acme-service-config` is a plain ConfigMap: a configuration-only change does not restart pods. Run `kubectl rollout restart deployment/acme-service -n <ns>` afterwards.
- `kubectl apply -k` applies the whole overlay: the image tag, the ConfigMap and every other manifest in it. Apply only revisions that are meant to be released.

### Replicas versus the HPA

The overlays set a fixed replica count (`replicas:` in each `kustomization.yaml`, for example 3 in prod) **and** a HorizontalPodAutoscaler. Every `kubectl apply -k deploy/k8s/overlays/<env>` therefore resets `spec.replicas` of the Deployment to the overlay value, even when the HPA has scaled out; the HPA scales back up afterwards, but for a moment the service runs with fewer pods than the load needs. Deploy during low load or watch the HPA after each apply. A follow-up change to the manifests should drop `replicas:` from the overlays and let the HPA's `minReplicas` alone set the floor; this documentation does not change the manifests.

Validate the manifests before applying (CI runs the same script):

```bash
bash scripts/ci/validate-k8s.sh      # kustomize build | kubeconform -strict, and no Secret objects rendered
```

## Probes and runtime

| Probe | Path | Meaning |
| --- | --- | --- |
| startup | `/health/live` | Up to 2 minutes (24 x 5 s) to start. |
| liveness | `/health/live` | The process only; a dependency outage never restarts pods. |
| readiness | `/health/ready` | PostgreSQL reachable and no pending migrations; Redis reports Degraded but stays 200. |

Pods run as user 1654 with a read-only root filesystem (a writable `/tmp` emptyDir), no privilege escalation, all capabilities dropped and the default seccomp profile. The chiseled runtime image has no shell, so every probe is an HTTP GET and debugging uses `kubectl debug` with an ephemeral container ([troubleshooting](troubleshooting.md#debugging-the-container)). Of the verbs, only `auth:grant-role` can run inside an app pod (it needs `ConnectionStrings:Postgres`, which the pod has); `migrate` and `migrate --list` need `ConnectionStrings:PostgresMigrations`, which only the migration Job receives, so they never run in app pods. The Service is `ClusterIP` on port 80; bring your own Ingress or Gateway and terminate TLS there.

## After the first deployment

1. Register the first user through the API, confirm the email, then grant the Admin role ([auth guide](../guides/auth.md#bootstrap-the-first-administrator)):

   ```bash
   kubectl exec -n <ns> deploy/acme-service -- dotnet Acme.Service.Host.dll auth:grant-role --email <email> --role Admin
   ```

   `kubectl exec` starts a second .NET process inside a running pod, sharing its memory limit (512Mi in the base, server GC via `DOTNET_gcServer=1`). The command host is small, but on a pod already near its limit the pair can be OOM-killed; run it on a lightly loaded pod, or run the verb in a one-off pod or a copy of the migration Job with `args: ["auth:grant-role", "--email", "<email>", "--role", "Admin"]` (it needs only `ConnectionStrings__Postgres`).

2. Check that `/health/ready` is 200 through the ingress, that the startup log shows the expected `Provider ... = ...` lines, and that there is no forwarded-headers warning.

## Expand/contract migrations

Old and new pods run side by side during a rolling update, and the migration runs before any new pod exists, so **every migration must work with the previous release's code**:

1. **Expand** (release N): add tables, nullable columns or columns with defaults, and indexes. Code writes both the old and the new shape if both are read.
2. **Migrate data**: backfill idempotently, in the migration or a background job.
3. **Contract** (release N+1 or later, once no running version reads the old shape): drop old columns or tables, add `NOT NULL`, narrow types.

Never rename or drop in the release that stops using something. Rollback is "redeploy the previous image"; there are no down migrations in the pipeline, which is safe only because of this rule. Details and examples: [migrations](migrations.md).

## Rollback

1. Set the previous image tag in the app overlay. Do not re-run the old release's migration Job: it would apply nothing (EF ignores applied migrations it does not know), but its seeder would reset the permission catalog to the old release's declarations, deleting permissions the newer release added (and their grants) until the next `migrate`.
2. `kubectl apply -k deploy/k8s/overlays/<env>`; the old pods pass readiness because the expand-only schema is compatible and the old code sees no pending migrations of its own.
3. If a contract migration already ran, roll forward instead: restore from backup is the only way back.

Caveats:

- After step 1 the app overlay and the migration overlay name different image tags. That is intended until the next forward deployment, which must set both again (the next migration Job then runs the newer release's migrations and seeder).
- `kubectl apply -k` also applies the overlay's ConfigMap. Roll configuration back together with the image, from the same revision of the repository; new keys the old release does not know are harmless, but a value the new release changed may not suit the old one.
- The old release validates its own configuration at startup: if the newer release removed or renamed a key the old one requires, old pods fail startup (and stay not ready while the new pods keep serving). Restore the key before rolling back.

## Docker Compose is not a production deployment

`deploy/compose/compose.yaml` binds every port to `127.0.0.1`, runs single instances without backups, uses the public Azurite development key and an unauthenticated Aspire dashboard. Use it for development and demos only.
