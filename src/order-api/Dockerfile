FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:4b1cdaa57eed2cecabcf29bdb9bce11e8ca1c287d39dfd2c8b534663ea94d493 AS build
WORKDIR /src
# Install corporate CA (Zscaler) so dotnet restore can reach NuGet over SSL.
# zcert.crt is copied to the build context by the Makefile build target.
COPY zcert.crt /usr/local/share/ca-certificates/zcert.crt
RUN update-ca-certificates
COPY order-api.csproj .
RUN dotnet restore
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
FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:f88c77644f4c480a62d3b46dc74db8d5472a24e282df8b1e56195c689d35a6db AS runtime
WORKDIR /app
COPY --from=build --chown=1654:1654 /app/publish .
USER 1654:1654
ENV ASPNETCORE_URLS=http://+:5001
EXPOSE 5001
ENTRYPOINT ["dotnet", "order-api.dll"]
