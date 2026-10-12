# syntax=docker/dockerfile:1.7
# Acme.Service Host image (plan P20). Multi-stage: the SDK stage builds, the chiseled runtime stage only runs.
# No ARG/ENV carries a secret (INV-11); all configuration, secrets included, arrives at run time as environment variables
# (for example the platform's env var store).
# The image runs the web app by default and does not migrate (INV-16). Schema changes run through the same image: either the
# `migrate` command (`dotnet Acme.Service.Host.dll migrate`, a one-shot run) or, opt-in, `Migrations__RunOnStart=true`, which
# migrates first and starts the web app only if that succeeded.
# Pin the base images by digest (name:tag@sha256:...) and let Dependabot keep the digests current.

# ---- Stage 1: dependency manifests only, so the restore layer is cached until a csproj/lock file changes ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS manifests
WORKDIR /src
COPY src/ src/
RUN find src -type f ! -name '*.csproj' ! -name 'packages.lock.json' -delete \
    && find src -type d -empty -delete

# ---- Stage 2: restore (locked) and publish ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY --from=manifests /src/src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/Host/Acme.Service.Host/Acme.Service.Host.csproj --locked-mode
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/Host/Acme.Service.Host/Acme.Service.Host.csproj \
      -c Release -o /app --no-restore /p:UseAppHost=false

# ---- Stage 3: runtime (chiseled Ubuntu, no shell or package manager, non-root user 1654, includes ICU and tzdata) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Acme.Service.Host.dll"]
