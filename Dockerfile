# Multi-arch Dockerfile for SimpleJadePinServer.Blazor
#
# Uses cross-compilation: dotnet publish with -r linux-<arch> runs natively
# on amd64 without QEMU emulation, producing binaries for each target arch.
# Only the final COPY into the runtime image needs the target platform.
#
# Usage:
#   docker build -t simplejadepinserver-blazor .
#   docker run -d -p 4443:8080 -v ./key_data:/app/key_data simplejadepinserver-blazor

# ── Build stage (always runs on amd64, cross-compiles for target) ────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
ARG TARGETARCH
WORKDIR /src

# Copy project files and restore for the target architecture
COPY SimpleJadePinServer.Blazor.slnx .
COPY src/SimpleJadePinServer.Blazor/SimpleJadePinServer.Blazor.csproj src/SimpleJadePinServer.Blazor/
COPY src/SimpleJadePinServer.Blazor.Crypto/SimpleJadePinServer.Blazor.Crypto.csproj src/SimpleJadePinServer.Blazor.Crypto/
COPY src/SimpleJadePinServer.Blazor.Services/SimpleJadePinServer.Blazor.Services.csproj src/SimpleJadePinServer.Blazor.Services/
RUN dotnet restore src/SimpleJadePinServer.Blazor/SimpleJadePinServer.Blazor.csproj -a $TARGETARCH

# Copy source and publish for the target architecture
COPY src/SimpleJadePinServer.Blazor/ src/SimpleJadePinServer.Blazor/
COPY src/SimpleJadePinServer.Blazor.Crypto/ src/SimpleJadePinServer.Blazor.Crypto/
COPY src/SimpleJadePinServer.Blazor.Services/ src/SimpleJadePinServer.Blazor.Services/
RUN dotnet publish src/SimpleJadePinServer.Blazor/SimpleJadePinServer.Blazor.csproj \
    --no-restore -a $TARGETARCH -c Release -o /app/publish

# ── Runtime stage (uses target platform's runtime image) ─────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .
VOLUME /app/key_data
EXPOSE 8080
ENTRYPOINT ["dotnet", "SimpleJadePinServer.Blazor.dll"]
