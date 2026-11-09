# GenePdf demo: dev server (API + studio UI) with Chromium, for the hosted demo.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY GenePdf.slnx ./
COPY src/GenePdf.Core/GenePdf.Core.csproj src/GenePdf.Core/
COPY src/GenePdf.DevServer/GenePdf.DevServer.csproj src/GenePdf.DevServer/
RUN dotnet restore src/GenePdf.DevServer/GenePdf.DevServer.csproj
COPY src/ src/
RUN dotnet publish src/GenePdf.DevServer/GenePdf.DevServer.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
RUN apk add --no-cache chromium font-dejavu icu-libs tzdata \
 && adduser -D -u 10001 genepdf
ENV TZ=Europe/Brussels \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    GenePdf__ChromiumPath=/usr/bin/chromium \
    GenePdf__TemplateRoot=/data/templates \
    GenePdf__SeedFrom=/app/templates \
    HOME=/tmp
WORKDIR /app
COPY --from=build /app ./
COPY templates/ /app/templates/
# /data is a volume in compose; created here so a new volume inherits the ownership.
RUN mkdir -p /data/templates && chown -R 10001 /data
USER 10001
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/health >/dev/null || exit 1
ENTRYPOINT ["dotnet", "GenePdf.DevServer.dll"]
