# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:4b1cdaa57eed2cecabcf29bdb9bce11e8ca1c287d39dfd2c8b534663ea94d493 AS build
WORKDIR /src
COPY gateway-api.csproj .
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
# gateway-api.csproj references ../proto (src/proto/ at the repo root — see
# that directory's OrderValidation.cs for why) via a relative <Protobuf
# Include>/<Compile Include>. The build context here is scoped to
# src/gateway-api/, so that path is normally invisible to Docker;
# deploy-local.sh's build_images() (stage_shared_proto) and the CI workflow
# both stage a copy at ./proto specifically so this COPY can put it where
# /src/../proto resolves to.
COPY proto/ /proto/
RUN dotnet publish -c Release -o /app/publish

# Runtime — use the aspnet:8.0 image's built-in non-root `app` user (UID 1654).
# K8s Deployment's securityContext.runAsUser must match.
FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:787c228ea85457bec43c8b084e6ac360b26ea43b5c2fcbe861f721f2e8670dd3 AS runtime
WORKDIR /app
COPY --from=build --chown=1654:1654 /app/publish .
USER 1654:1654
# 0.0.0.0, not + — see k8s/app/gateway/deployment.yaml's ASPNETCORE_URLS
# comment for why "+" isn't safe to rely on for IPv4 connectivity.
ENV ASPNETCORE_URLS=http://0.0.0.0:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "gateway-api.dll"]
