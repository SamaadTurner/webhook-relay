# Build stage: has the full SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the project file first so the restore step is cached
# until a package reference changes
COPY global.json ./
COPY src/WebhookRelay.Api/WebhookRelay.Api.csproj src/WebhookRelay.Api/
RUN dotnet restore src/WebhookRelay.Api/WebhookRelay.Api.csproj

COPY src/ src/
RUN dotnet publish src/WebhookRelay.Api/WebhookRelay.Api.csproj -c Release -o /app --no-restore

# Run stage: only the ASP.NET runtime, so the image is much smaller
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# The SQLite file lives in /data so it can sit on a volume and survive a new container.
# APP_UID is the non-root user that comes with the image.
USER root
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080
ENV ConnectionStrings__Relay="Data Source=/data/relay.db"
EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "WebhookRelay.Api.dll"]
