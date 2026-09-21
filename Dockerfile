# syntax=docker/dockerfile:1
# Self-contained: builds from this repo's own tree only. Nothing outside the build context is referenced.
# Native AOT: the runtime image needs no .NET runtime at all, only the chiselled native dependencies.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
# NativeAOT cannot cross-compile, so each platform is built natively (buildx runs the arm64 leg under
# QEMU or on an arm64 runner) — hence no --platform=$BUILDPLATFORM on this stage.
RUN case "$TARGETARCH" in amd64) rid=linux-x64;; arm64) rid=linux-arm64;; *) echo "unsupported $TARGETARCH"; exit 1;; esac; \
    dotnet publish src/RuleEngine/RuleEngine.csproj -c Release -r $rid -p:Version=$VERSION -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime
ARG VERSION=0.0.0
ARG REVISION=unknown
LABEL org.opencontainers.image.title="rule-engine-service" \
      org.opencontainers.image.source="https://github.com/bklooste/rule-engine-service" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"
WORKDIR /app
COPY --from=build /app/RuleEngine .
# The chiselled base defaults to the non-root `app` user; stated explicitly so it can't regress.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# No shell/curl in a chiselled image, so the app probes itself.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["/app/RuleEngine", "--healthcheck"]
ENTRYPOINT ["/app/RuleEngine"]
