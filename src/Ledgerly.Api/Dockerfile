# syntax=docker/dockerfile:1

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ledgerly.Domain/Ledgerly.Domain.csproj src/Ledgerly.Domain/
COPY src/Ledgerly.Api/Ledgerly.Api.csproj src/Ledgerly.Api/
RUN dotnet restore src/Ledgerly.Api/Ledgerly.Api.csproj

COPY src/ src/
RUN dotnet publish src/Ledgerly.Api/Ledgerly.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---------- runtime ----------
# Chiseled: no shell or package manager, runs as the non-root "app" user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

# HTTP/1.1 (REST via JSON transcoding) and HTTP/2 (gRPC) on the same port.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Kestrel__EndpointDefaults__Protocols=Http1AndHttp2

COPY --from=build /app/publish .

EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Ledgerly.Api.dll"]
