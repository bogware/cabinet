# Multi-stage Dockerfile for Cabinet
# Optimized for size and security

# Stage 1: Build environment
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Copy solution and project files
COPY Cabinet.sln ./
COPY src/Cabinet.Core/*.csproj ./src/Cabinet.Core/
COPY src/Cabinet.Parsers/*.csproj ./src/Cabinet.Parsers/
COPY src/Cabinet.Storage/*.csproj ./src/Cabinet.Storage/
COPY src/Cabinet.Query/*.csproj ./src/Cabinet.Query/
COPY src/Cabinet.Detection/*.csproj ./src/Cabinet.Detection/
COPY src/Cabinet.Alerts/*.csproj ./src/Cabinet.Alerts/
COPY src/Cabinet.AWS/*.csproj ./src/Cabinet.AWS/
COPY src/Cabinet.CLI/*.csproj ./src/Cabinet.CLI/
COPY tests/Cabinet.Tests/*.csproj ./tests/Cabinet.Tests/

# Restore dependencies
RUN dotnet restore

# Copy source code
COPY src/ ./src/
COPY tests/ ./tests/

# Build and test
RUN dotnet build -c Release --no-restore
RUN dotnet test -c Release --no-build --verbosity normal

# Publish the application
RUN dotnet publish src/Cabinet.CLI/Cabinet.CLI.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    --self-contained false \
    /p:PublishSingleFile=true \
    /p:PublishTrimmed=true

# Stage 2: Runtime environment
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime

# Install required packages
RUN apk add --no-cache \
    ca-certificates \
    icu-libs \
    tzdata \
    && rm -rf /var/cache/apk/*

# Create non-root user
RUN addgroup -g 1000 cabinet && \
    adduser -u 1000 -G cabinet -s /bin/sh -D cabinet

# Set working directory
WORKDIR /app

# Copy published application
COPY --from=build --chown=cabinet:cabinet /app/publish .

# Create directories for data and logs
RUN mkdir -p /data /logs /config && \
    chown -R cabinet:cabinet /data /logs /config

# Set environment variables
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=en_US.UTF-8 \
    LANG=en_US.UTF-8 \
    CABINET_DATA_PATH=/data \
    CABINET_LOG_PATH=/logs \
    CABINET_CONFIG_PATH=/config

# Switch to non-root user
USER cabinet

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD ["./cabinet", "--version"]

# Volumes for persistent data
VOLUME ["/data", "/logs", "/config"]

# Entry point
ENTRYPOINT ["./cabinet"]
CMD ["--help"]