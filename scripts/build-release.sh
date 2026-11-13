#!/usr/bin/env bash
# Cabinet Release Build Script
# Builds all platform artifacts for distribution.
#
# Usage:
#   ./scripts/build-release.sh [version]
#
# Examples:
#   ./scripts/build-release.sh 1.0.0
#   ./scripts/build-release.sh          # uses git tag or "dev"
#
# Output: dist/ directory with all artifacts
#   dist/
#   ├── cabinet-api-linux-x64.tar.gz
#   ├── cabinet-api-linux-arm64.tar.gz
#   ├── cabinet-api-win-x64.zip
#   ├── cabinet-api-osx-x64.tar.gz
#   ├── cabinet-api-osx-arm64.tar.gz
#   ├── cabinet-cli-{platform}.{ext}
#   ├── cabinet-agent-{platform}.{ext}
#   ├── cabinet-server_1.0.0_amd64.deb     (if dpkg-deb available)
#   ├── cabinet-agent_1.0.0_amd64.deb      (if dpkg-deb available)
#   ├── *.sha256                            (checksums)
#   └── install scripts

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DIST_DIR="$ROOT_DIR/dist"

# Version
VERSION="${1:-$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || echo "dev")}"
echo ""
echo "Building Cabinet v$VERSION"
echo "========================="
echo ""

# Platforms to build
PLATFORMS=(
    "linux-x64"
    "linux-arm64"
    "win-x64"
    "osx-x64"
    "osx-arm64"
)

# Components
COMPONENTS=(
    "src/Cabinet.API:Cabinet.API:cabinet-api"
    "src/Cabinet.CLI:cabinet:cabinet-cli"
    "src/Cabinet.Agent:cabinet-agent:cabinet-agent"
)

# Clean
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR/staging"

# ─── Build each component for each platform ───────────────────────────
for component_spec in "${COMPONENTS[@]}"; do
    IFS=':' read -r project_path binary_name artifact_prefix <<< "$component_spec"

    for platform in "${PLATFORMS[@]}"; do
        echo "Building $artifact_prefix for $platform..."

        publish_dir="$DIST_DIR/staging/$artifact_prefix-$platform"

        dotnet publish "$ROOT_DIR/$project_path" \
            -c Release \
            -r "$platform" \
            --self-contained true \
            /p:PublishSingleFile=true \
            /p:PublishTrimmed=false \
            /p:Version="$VERSION" \
            -o "$publish_dir" \
            --nologo -v quiet 2>/dev/null || {
                echo "  Warning: Failed to build $artifact_prefix for $platform (skipping)"
                continue
            }

        # Package
        artifact_name="$artifact_prefix-$VERSION-$platform"

        if [[ "$platform" == win-* ]]; then
            # Windows: zip
            if command -v zip &>/dev/null; then
                (cd "$publish_dir" && zip -q -r "$DIST_DIR/$artifact_name.zip" .)
            else
                # Fallback to tar.gz even for Windows
                tar -czf "$DIST_DIR/$artifact_name.tar.gz" -C "$publish_dir" .
            fi
        else
            # Linux/macOS: tar.gz
            tar -czf "$DIST_DIR/$artifact_name.tar.gz" -C "$publish_dir" .
        fi

        echo "  -> $artifact_name"
    done
done

# ─── Build .deb packages (Linux only) ─────────────────────────────────
if command -v dpkg-deb &>/dev/null; then
    echo ""
    echo "Building .deb packages..."

    for deb_spec in "cabinet-api:api:Cabinet.API" "cabinet-agent:agent:cabinet-agent"; do
        IFS=':' read -r pkg_name component binary <<< "$deb_spec"

        for arch_pair in "linux-x64:amd64" "linux-arm64:arm64"; do
            IFS=':' read -r platform deb_arch <<< "$arch_pair"

            src_dir="$DIST_DIR/staging/$pkg_name-$platform"
            [[ -d "$src_dir" ]] || continue

            deb_dir="$DIST_DIR/staging/deb-$pkg_name-$deb_arch"
            rm -rf "$deb_dir"
            mkdir -p "$deb_dir/DEBIAN"
            mkdir -p "$deb_dir/opt/cabinet/$component"
            mkdir -p "$deb_dir/etc/cabinet"
            mkdir -p "$deb_dir/var/lib/cabinet"
            mkdir -p "$deb_dir/lib/systemd/system"

            # Copy binary
            cp -r "$src_dir"/* "$deb_dir/opt/cabinet/$component/"

            # DEBIAN/control
            cat > "$deb_dir/DEBIAN/control" <<CTLEOF
Package: $pkg_name
Version: $VERSION
Section: admin
Priority: optional
Architecture: $deb_arch
Maintainer: Cabinet Team <cabinet@example.com>
Description: Cabinet Log Intelligence System - ${component^} component
 Cabinet is a log management and intelligence system designed as a
 Splunk replacement. This package contains the $component component.
Homepage: https://github.com/bogware/cabinet
CTLEOF

            # DEBIAN/postinst
            if [[ "$component" == "api" ]]; then
                cat > "$deb_dir/DEBIAN/postinst" <<'POSTEOF'
#!/bin/bash
set -e

# Create service user
if ! id cabinet &>/dev/null; then
    useradd --system --no-create-home --shell /sbin/nologin cabinet
fi

# Set permissions
mkdir -p /var/lib/cabinet/logs /var/lib/cabinet/settings /var/lib/cabinet/updates
chown -R cabinet:cabinet /var/lib/cabinet
# The program stays root's: the service can run it but not change it
chown -R root:root /opt/cabinet/api
chmod +x /opt/cabinet/api/Cabinet.API

# Create symlink for CLI if present
if [ -f /opt/cabinet/cli/cabinet ]; then
    ln -sf /opt/cabinet/cli/cabinet /usr/local/bin/cabinet
fi

# Enable and start service
systemctl daemon-reload
systemctl enable cabinet-api.service || true
echo ""
echo "Cabinet API installed. To start:"
echo "  sudo systemctl start cabinet-api"
echo "  Dashboard: http://localhost:8080/"
POSTEOF
                chmod 755 "$deb_dir/DEBIAN/postinst"

                # DEBIAN/prerm
                cat > "$deb_dir/DEBIAN/prerm" <<'PRERMEOF'
#!/bin/bash
set -e
systemctl stop cabinet-api.service 2>/dev/null || true
systemctl disable cabinet-api.service 2>/dev/null || true
PRERMEOF
                chmod 755 "$deb_dir/DEBIAN/prerm"

                # systemd service file
                cat > "$deb_dir/lib/systemd/system/cabinet-api.service" <<SVCEOF
[Unit]
Description=Cabinet API Server - Log Intelligence System
Documentation=https://github.com/bogware/cabinet
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=/opt/cabinet/api/Cabinet.API --urls http://0.0.0.0:8080
WorkingDirectory=/opt/cabinet/api
Restart=on-failure
RestartSec=10
User=cabinet
Group=cabinet
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ReadWritePaths=/var/lib/cabinet
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=CABINET_LOG_DIR=/var/lib/cabinet/logs
Environment=CABINET_SETTINGS_FILE=/var/lib/cabinet/settings/cabinet.settings.json
Environment=Updates__WorkDirectory=/var/lib/cabinet/updates
EnvironmentFile=-/etc/cabinet/cabinet.env
StandardOutput=journal
StandardError=journal
SyslogIdentifier=cabinet-api
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
SVCEOF
            elif [[ "$component" == "agent" ]]; then
                cat > "$deb_dir/DEBIAN/postinst" <<'POSTEOF'
#!/bin/bash
set -e

if ! id cabinet-agent &>/dev/null; then
    useradd --system --no-create-home --shell /sbin/nologin cabinet-agent
fi

chown -R cabinet-agent:cabinet-agent /opt/cabinet/agent
chmod +x /opt/cabinet/agent/cabinet-agent

systemctl daemon-reload
systemctl enable cabinet-agent.service || true
echo ""
echo "Cabinet Agent installed. Configure /opt/cabinet/agent/appsettings.json then:"
echo "  sudo systemctl start cabinet-agent"
POSTEOF
                chmod 755 "$deb_dir/DEBIAN/postinst"

                cat > "$deb_dir/DEBIAN/prerm" <<'PRERMEOF'
#!/bin/bash
set -e
systemctl stop cabinet-agent.service 2>/dev/null || true
systemctl disable cabinet-agent.service 2>/dev/null || true
PRERMEOF
                chmod 755 "$deb_dir/DEBIAN/prerm"

                cat > "$deb_dir/lib/systemd/system/cabinet-agent.service" <<SVCEOF
[Unit]
Description=Cabinet Log Forwarding Agent
Documentation=https://github.com/bogware/cabinet
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=/opt/cabinet/agent/cabinet-agent
WorkingDirectory=/opt/cabinet/agent
Restart=always
RestartSec=5
User=cabinet-agent
Group=cabinet-agent
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ReadWritePaths=/opt/cabinet/agent /var/log
StandardOutput=journal
StandardError=journal
SyslogIdentifier=cabinet-agent

[Install]
WantedBy=multi-user.target
SVCEOF
            fi

            # Build .deb
            deb_file="$DIST_DIR/${pkg_name}_${VERSION}_${deb_arch}.deb"
            dpkg-deb --build "$deb_dir" "$deb_file" 2>/dev/null
            echo "  -> ${pkg_name}_${VERSION}_${deb_arch}.deb"
        done
    done
else
    echo ""
    echo "Note: dpkg-deb not found. Skipping .deb package creation."
    echo "  Install with: apt-get install dpkg-dev"
fi

# ─── Copy install scripts ─────────────────────────────────────────────
echo ""
echo "Copying install scripts..."
cp "$SCRIPT_DIR/install-server.sh" "$DIST_DIR/"
cp "$SCRIPT_DIR/install-server.ps1" "$DIST_DIR/"
cp "$SCRIPT_DIR/install-agent.sh" "$DIST_DIR/"
cp "$SCRIPT_DIR/install-agent.ps1" "$DIST_DIR/"

# ─── Generate checksums ───────────────────────────────────────────────
echo ""
echo "Generating checksums..."
cd "$DIST_DIR"
shopt -s nullglob
for f in *.tar.gz *.zip *.deb; do
    [[ -f "$f" ]] || continue
    sha256sum "$f" > "$f.sha256"
done

# ─── Clean staging ────────────────────────────────────────────────────
rm -rf "$DIST_DIR/staging"

# ─── Summary ──────────────────────────────────────────────────────────
echo ""
echo "Build complete! Artifacts in $DIST_DIR/"
echo ""
ls -lh "$DIST_DIR"/ 2>/dev/null | grep -v total
echo ""
echo "Distribution guide:"
echo "  Linux (Debian/Ubuntu):  sudo dpkg -i cabinet-api_${VERSION}_amd64.deb"
echo "  Linux (any):            sudo ./install-server.sh"
echo "  Windows:                .\\install-server.ps1 (as Administrator)"
echo "  Docker:                 docker-compose up"
echo "  Agent (Linux):          sudo ./install-agent.sh --server URL --api-key KEY"
echo "  Agent (Windows):        .\\install-agent.ps1 -ServerUrl URL -ApiKey KEY"
