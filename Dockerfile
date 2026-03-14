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

# Copy all source files
COPY src/SimpleJadePinServer.Blazor/ src/SimpleJadePinServer.Blazor/
COPY src/SimpleJadePinServer.Blazor.Crypto/ src/SimpleJadePinServer.Blazor.Crypto/
COPY src/SimpleJadePinServer.Blazor.Services/ src/SimpleJadePinServer.Blazor.Services/

# Publish for the target architecture (restore + build + publish in one step)
RUN dotnet publish src/SimpleJadePinServer.Blazor/SimpleJadePinServer.Blazor.csproj \
    -a $TARGETARCH -c Release -o /app/publish

# ── Runtime stage (uses target platform's runtime image) ─────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .
VOLUME /app/key_data
EXPOSE 8080
ENTRYPOINT ["dotnet", "SimpleJadePinServer.Blazor.dll"]
