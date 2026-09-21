# Build context = repo root: docker build -f tst/RuleEngine.WrappedHost/Dockerfile -t rule-engine-wrapped:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN dotnet publish tst/RuleEngine.WrappedHost/RuleEngine.WrappedHost.csproj -c Release -r linux-x64 -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app/RuleEngine.WrappedHost .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["/app/RuleEngine.WrappedHost"]
