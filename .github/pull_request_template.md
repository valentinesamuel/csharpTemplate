## Summary

<!-- What changes and why. Link the issue/plan phase. -->

## Type of change

- [ ] Feature
- [ ] Bug fix
- [ ] Refactor / chore
- [ ] Dependencies
- [ ] CI / deployment

## Checklist

- [ ] `dotnet build -c Release` passes with no warnings (warnings are errors).
- [ ] `dotnet format --verify-no-changes` passes.
- [ ] Unit and architecture tests pass: `dotnet test Acme.Service.slnx -c Release --filter "Category!=Integration"`.
- [ ] Integration tests pass: `dotnet test Acme.Service.slnx -c Release --filter "Category=Integration"`.
- [ ] Package versions only in `Directory.Packages.props`; lock files updated and committed (`--locked-mode` restore passes).
- [ ] New packages have a permissive licence and are recorded in the Package Ledger (no MediatR, AutoMapper, FluentAssertions v8+, Moq, MassTransit v9+).
- [ ] No secrets in code, config, Dockerfile, build args or logs (`.env` stays local; only placeholders in `.env.example`).
- [ ] Schema changes ship as an EF migration applied by the `migrate` command, never at startup.
- [ ] Kubernetes manifests still validate (`scripts/ci/validate-k8s.sh`) if `deploy/` changed.
- [ ] Docs / README updated where behaviour or configuration changed.

## Testing notes

<!-- How this was verified; anything reviewers should try. -->

## Deployment / rollback notes

<!-- New configuration keys, migrations, feature flags, ordering concerns. "None" if not applicable. -->
