# syntax=docker/dockerfile:1
# One multi-stage build, two runnable targets:
#   docker build --target api      -t inventory-analytics-api .
#   docker build --target migrator -t inventory-analytics-migrator .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, from project files only, so the NuGet layer is cached across code changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/InventoryAnalytics.Core/InventoryAnalytics.Core.csproj                     src/InventoryAnalytics.Core/
COPY src/InventoryAnalytics.Infrastructure/InventoryAnalytics.Infrastructure.csproj src/InventoryAnalytics.Infrastructure/
COPY src/InventoryAnalytics.Api/InventoryAnalytics.Api.csproj                       src/InventoryAnalytics.Api/
COPY src/InventoryAnalytics.Migrator/InventoryAnalytics.Migrator.csproj             src/InventoryAnalytics.Migrator/
RUN dotnet restore src/InventoryAnalytics.Api/InventoryAnalytics.Api.csproj \
 && dotnet restore src/InventoryAnalytics.Migrator/InventoryAnalytics.Migrator.csproj

COPY src/ src/
COPY database/ database/
RUN dotnet publish src/InventoryAnalytics.Api/InventoryAnalytics.Api.csproj -c Release -o /out/api --no-restore \
 && dotnet publish src/InventoryAnalytics.Migrator/InventoryAnalytics.Migrator.csproj -c Release -o /out/migrator --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Non-root user provided by the official .NET 8 images.
USER $APP_UID
ENTRYPOINT ["dotnet", "InventoryAnalytics.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS migrator
WORKDIR /app
COPY --from=build /out/migrator .
USER $APP_UID
ENTRYPOINT ["dotnet", "InventoryAnalytics.Migrator.dll"]
