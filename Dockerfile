# Multi-stage Dockerfile for SimpleJadePinServer.Blazor
#
# Supports multi-arch builds (amd64 + arm64) for Umbrel/Raspberry Pi compatibility.
#
# Usage:
#   docker build -t simplejadepinserver-blazor .
#   docker run -d -p 4443:8080 -v ./key_data:/app/key_data simplejadepinserver-blazor

# ── Build stage ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Copy solution and project files first (layer caching — only re-restores when csproj changes)
COPY SimpleJadePinServer.Blazor.slnx .
COPY src/SimpleJadePinServer.Blazor/SimpleJadePinServer.Blazor.csproj src/SimpleJadePinServer.Blazor/
COPY src/SimpleJadePinServer.Blazor.Crypto/SimpleJadePinServer.Blazor.Crypto.csproj src/SimpleJadePinServer.Blazor.Crypto/
COPY src/SimpleJadePinServer.Blazor.Services/SimpleJadePinServer.Blazor.Services.csproj src/SimpleJadePinServer.Blazor.Services/
COPY src/SimpleJadePinServer.Blazor.Tests/SimpleJadePinServer.Blazor.Tests.csproj src/SimpleJadePinServer.Blazor.Tests/
RUN dotnet restore SimpleJadePinServer.Blazor.slnx

# Copy all source (tests run in the CI workflow before docker build, not here —
# QEMU arm64 emulation is too slow for the vstest connection timeout)
COPY src/ src/

# Publish the web app
RUN dotnet publish src/SimpleJadePinServer.Blazor/SimpleJadePinServer.Blazor.csproj \
    --no-restore -c Release -o /app/publish

# ── Runtime stage ────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .
VOLUME /app/key_data
EXPOSE 8080
ENTRYPOINT ["dotnet", "SimpleJadePinServer.Blazor.dll"]
