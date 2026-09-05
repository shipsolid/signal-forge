# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:4b1cdaa57eed2cecabcf29bdb9bce11e8ca1c287d39dfd2c8b534663ea94d493 AS build
WORKDIR /src
COPY order-api.csproj .
# The local build path may expose the corporate CA as a BuildKit secret. The
# temporary combined bundle exists only for this restore command; CI omits it.
RUN --mount=type=secret,id=corporate_ca,required=false \
    if [ -s /run/secrets/corporate_ca ]; then \
      ca_bundle="$(mktemp)"; \
      cat /etc/ssl/certs/ca-certificates.crt /run/secrets/corporate_ca > "$ca_bundle"; \
      SSL_CERT_FILE="$ca_bundle" dotnet restore; \
      rm -f "$ca_bundle"; \
    else \
      dotnet restore; \
    fi
COPY . .
# order-api.csproj references ../proto (src/proto/ at the repo root — see that
# directory's OrderValidation.cs for why) via a relative <Protobuf Include>/
# <Compile Include>. The build context here is scoped to src/order-api/, so
# that path is normally invisible to Docker; deploy-local.sh's build_images()
# (stage_shared_proto) and the CI workflow both stage a copy at ./proto
# specifically so this COPY can put it where /src/../proto resolves to.
COPY proto/ /proto/
RUN dotnet publish -c Release -o /app/publish

# Runtime — use the aspnet:8.0 image's built-in non-root `app` user (UID 1654).
# K8s Deployment's securityContext.runAsUser must match.
FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:787c228ea85457bec43c8b084e6ac360b26ea43b5c2fcbe861f721f2e8670dd3 AS runtime
WORKDIR /app
COPY --from=build --chown=1654:1654 /app/publish .
USER 1654:1654
# No ASPNETCORE_URLS — Program.cs's ConfigureKestrel binds both ports
# explicitly (5001 HTTP/1.1 for /healthz, 5002 HTTP/2-only for gRPC; see that
# file's "gRPC server" comment for why they're split). Setting ASPNETCORE_URLS
# here too would make Kestrel additionally try the URLs-based binding path,
# double-binding port 5001 and crashing at startup.
EXPOSE 5001 5002
ENTRYPOINT ["dotnet", "order-api.dll"]
