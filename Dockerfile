# SecretariArt in a container: build with the SDK, run on the smaller ASP.NET image as a non-root user.
#   docker build -t secretariart .
# See docker-compose.yml for the app with a SQL Server next to it, and README "Deploying" for the settings.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
# Restore first, from the project file alone, so the packages layer is cached until dependencies change.
COPY src/SecretariArt/SecretariArt.csproj src/SecretariArt/
RUN dotnet restore src/SecretariArt/SecretariArt.csproj
COPY src/SecretariArt/ src/SecretariArt/
RUN dotnet publish src/SecretariArt/SecretariArt.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Sign-in keys and saved emails live outside the image (mount volumes here), so they survive a new version.
RUN mkdir -p /data/keys /app/App_Data && chown -R $APP_UID /data /app/App_Data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DataProtection__KeysDirectory=/data/keys
USER $APP_UID
EXPOSE 8080
# Health: GET /health (200 when the database is reachable), for the load balancer or orchestrator to probe.
ENTRYPOINT ["dotnet", "SecretariArt.dll"]
