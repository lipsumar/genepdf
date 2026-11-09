# Local development image: .NET 10 SDK + Chromium, so nothing needs to be installed on the Mac.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine
RUN apk add --no-cache chromium font-noto font-dejavu icu-libs
ENV GENEPDF__CHROMIUMPATH=/usr/bin/chromium \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
