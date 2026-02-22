#!/usr/bin/env bash
# Cabinet API Server Installer for Linux
# Installs Cabinet API as a systemd service with proper security hardening.
#
# Usage:
#   sudo ./install-server.sh [options]
#
# Options:
#   --install-dir DIR   Installation directory (default: /opt/cabinet)
#   --port PORT         HTTP port (default: 8080)
#   --storage TYPE      Storage backend: sqlite, postgresql, opensearch (default: sqlite)
#   --conn-string STR   Storage connection string
#   --data-dir DIR      Data directory for SQLite/backups (default: /var/lib/cabinet)
#   --run-wizard        Launch the interactive setup wizard after install
#   --help              Show this help

set -euo pipefail

# ─── Defaults ──────────────────────────────────────────────────────────
INSTALL_DIR="/opt/cabinet"
DATA_DIR="/var/lib/cabinet"
CONFIG_DIR="/etc/cabinet"
SERVICE_USER="cabinet"
PORT=8080
STORAGE_TYPE="sqlite"
CONN_STRING=""
RUN_WIZARD=false
COMPONENT="api"  # api, cli, all

# ─── Parse arguments ──────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)  INSTALL_DIR="$2"; shift 2 ;;
        --data-dir)     DATA_DIR="$2"; shift 2 ;;
        --port)         PORT="$2"; shift 2 ;;
        --storage)      STORAGE_TYPE="$2"; shift 2 ;;
        --conn-string)  CONN_STRING="$2"; shift 2 ;;
        --run-wizard)   RUN_WIZARD=true; shift ;;
        --help)
            head -14 "$0" | tail -13 | sed 's/^# \?//'
            exit 0 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

# ─── Detect platform ──────────────────────────────────────────────────
OS=$(uname -s | tr '[:upper:]' '[:lower:]')
ARCH=$(uname -m)

case "$ARCH" in
    x86_64)  ARCH_SUFFIX="x64" ;;
    aarch64|arm64) ARCH_SUFFIX="arm64" ;;
    *) echo "Error: Unsupported architecture: $ARCH"; exit 1 ;;
esac

case "$OS" in
    linux)  RID="linux-${ARCH_SUFFIX}" ;;
    darwin) RID="osx-${ARCH_SUFFIX}" ;;
    *) echo "Error: Unsupported OS: $OS (use Linux or macOS)"; exit 1 ;;
esac

# ─── Banner ────────────────────────────────────────────────────────────
echo ""
echo "  ╔═══════════════════════════════════════╗"
echo "  ║   Cabinet API Server Installer        ║"
echo "  ║   Log Intelligence System             ║"
echo "  ╚═══════════════════════════════════════╝"
echo ""
echo "  Platform:    $RID"
echo "  Install dir: $INSTALL_DIR"
echo "  Data dir:    $DATA_DIR"
echo "  Config dir:  $CONFIG_DIR"
echo "  Port:        $PORT"
echo "  Storage:     $STORAGE_TYPE"
echo ""

# ─── Root check ────────────────────────────────────────────────────────
if [[ $EUID -ne 0 ]]; then
    echo "Error: This installer must be run as root (or with sudo)."
    echo "  sudo $0 $*"
    exit 1
fi

# ─── Create service user ──────────────────────────────────────────────
if ! id "$SERVICE_USER" &>/dev/null; then
    echo "[1/7] Creating service user: $SERVICE_USER"
    useradd --system --no-create-home --shell /sbin/nologin "$SERVICE_USER" 2>/dev/null || \
    adduser --system --no-create-home --disabled-login "$SERVICE_USER" 2>/dev/null || true
else
    echo "[1/7] Service user $SERVICE_USER already exists"
fi

# ─── Create directories ───────────────────────────────────────────────
echo "[2/7] Creating directories"
mkdir -p "$INSTALL_DIR"/{api,cli}
mkdir -p "$DATA_DIR"/{db,backups,logs}
mkdir -p "$CONFIG_DIR"

# ─── Check/copy binaries ──────────────────────────────────────────────
echo "[3/7] Checking for Cabinet binaries"

BINARY_FOUND=false
if [[ -f "$INSTALL_DIR/api/Cabinet.API" ]]; then
    BINARY_FOUND=true
    echo "  Found existing API binary"
elif [[ -f "./Cabinet.API" ]]; then
    echo "  Copying API binary from current directory"
    cp -r ./Cabinet.API* "$INSTALL_DIR/api/" 2>/dev/null || true
    cp -r ./appsettings*.json "$INSTALL_DIR/api/" 2>/dev/null || true
    cp -r ./wwwroot "$INSTALL_DIR/api/" 2>/dev/null || true
    BINARY_FOUND=true
fi

if [[ -f "./cabinet" ]] || [[ -f "./cabinet.dll" ]]; then
    echo "  Copying CLI binary from current directory"
    cp -r ./cabinet* "$INSTALL_DIR/cli/" 2>/dev/null || true
fi

if [[ "$BINARY_FOUND" = false ]]; then
    echo ""
    echo "  NOTE: No Cabinet.API binary found."
    echo "  After installation, copy the binary for your platform ($RID) to:"
    echo "    $INSTALL_DIR/api/"
    echo ""
    echo "  Download from: https://github.com/draxios/cabinet/releases"
    echo ""
fi

# ─── Generate configuration ───────────────────────────────────────────
echo "[4/7] Generating configuration"

# Determine connection string
case "$STORAGE_TYPE" in
    sqlite)
        STORAGE_TYPE_CFG="SQLite"
        [[ -z "$CONN_STRING" ]] && CONN_STRING="Data Source=$DATA_DIR/db/cabinet.db"
        ;;
    postgresql|postgres)
        STORAGE_TYPE_CFG="PostgreSQL"
        [[ -z "$CONN_STRING" ]] && CONN_STRING="SET_VIA_ENV_VAR"
        ;;
    opensearch|elasticsearch)
        STORAGE_TYPE_CFG="OpenSearch"
        [[ -z "$CONN_STRING" ]] && CONN_STRING="SET_VIA_ENV_VAR"
        ;;
    *)
        echo "Error: Unknown storage type: $STORAGE_TYPE"
        echo "  Valid options: sqlite, postgresql, opensearch"
        exit 1
        ;;
esac

if [[ ! -f "$CONFIG_DIR/appsettings.json" ]]; then
    cat > "$CONFIG_DIR/appsettings.json" <<CFGEOF
{
  "Urls": "http://0.0.0.0:$PORT",
  "Storage": {
    "Type": "$STORAGE_TYPE_CFG",
    "ConnectionString": "$CONN_STRING"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "RateLimiting": {
    "Basic": { "RequestsPerMinute": 100 },
    "Professional": { "RequestsPerMinute": 1000 },
    "Enterprise": { "RequestsPerMinute": 10000 }
  },
  "Cors": {
    "AllowedOrigins": ["http://localhost:3000"]
  },
  "Backup": {
    "ArchivePath": "$DATA_DIR/backups",
    "RetentionDays": 30,
    "AutoBackupEnabled": false
  }
}
CFGEOF
    echo "  Configuration written to $CONFIG_DIR/appsettings.json"
else
    echo "  Configuration already exists at $CONFIG_DIR/appsettings.json"
fi

# Create environment file for secrets
if [[ ! -f "$CONFIG_DIR/cabinet.env" ]]; then
    cat > "$CONFIG_DIR/cabinet.env" <<ENVEOF
# Cabinet API environment variables
# Secrets and overrides go here (this file is chmod 600)
ASPNETCORE_ENVIRONMENT=Production
# Storage__ConnectionString=Host=localhost;Database=cabinet;Username=cabinet;Password=secret
# ApiKeys__Keys__0__Key=your-production-api-key
# ApiKeys__Keys__0__ClientName=production
# ApiKeys__Keys__0__Tier=Enterprise
ENVEOF
    chmod 600 "$CONFIG_DIR/cabinet.env"
    echo "  Environment file written to $CONFIG_DIR/cabinet.env"
fi

# Symlink config into install dir so the binary finds it
ln -sf "$CONFIG_DIR/appsettings.json" "$INSTALL_DIR/api/appsettings.Production.json" 2>/dev/null || true

# ─── Set ownership ─────────────────────────────────────────────────────
echo "[5/7] Setting ownership and permissions"
chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"
chown -R root:root "$CONFIG_DIR"
chmod 644 "$CONFIG_DIR/appsettings.json"
chmod 600 "$CONFIG_DIR/cabinet.env"
# Binary needs to be executable
chmod +x "$INSTALL_DIR/api/Cabinet.API" 2>/dev/null || true
chmod +x "$INSTALL_DIR/cli/cabinet" 2>/dev/null || true

# ─── Install systemd service ──────────────────────────────────────────
echo "[6/7] Installing systemd service"

if [[ -d /etc/systemd/system ]]; then
    cat > /etc/systemd/system/cabinet-api.service <<SVCEOF
[Unit]
Description=Cabinet API Server - Log Intelligence System
Documentation=https://github.com/draxios/cabinet
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=$INSTALL_DIR/api/Cabinet.API --urls http://0.0.0.0:$PORT
WorkingDirectory=$INSTALL_DIR/api
Restart=on-failure
RestartSec=10
WatchdogSec=60
KillSignal=SIGTERM
TimeoutStopSec=30

User=$SERVICE_USER
Group=$SERVICE_USER
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
ReadWritePaths=$DATA_DIR $INSTALL_DIR/api
ReadOnlyPaths=$CONFIG_DIR

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ENVIRONMENT=Production
EnvironmentFile=-$CONFIG_DIR/cabinet.env

StandardOutput=journal
StandardError=journal
SyslogIdentifier=cabinet-api

# Resource limits
LimitNOFILE=65535
LimitNPROC=4096

[Install]
WantedBy=multi-user.target
SVCEOF

    systemctl daemon-reload
    echo "  Systemd service installed: cabinet-api"
else
    echo "  Warning: systemd not found. You'll need to start Cabinet manually."
fi

# ─── CLI symlink ───────────────────────────────────────────────────────
echo "[7/7] Setting up CLI"
if [[ -f "$INSTALL_DIR/cli/cabinet" ]]; then
    ln -sf "$INSTALL_DIR/cli/cabinet" /usr/local/bin/cabinet 2>/dev/null || true
    echo "  CLI available as: cabinet"
fi

# ─── Done ──────────────────────────────────────────────────────────────
echo ""
echo "  Installation complete!"
echo ""
echo "  ┌─────────────────────────────────────────────────┐"
echo "  │  Quick Start                                    │"
echo "  ├─────────────────────────────────────────────────┤"
echo "  │  Start:    systemctl start cabinet-api          │"
echo "  │  Enable:   systemctl enable cabinet-api         │"
echo "  │  Status:   systemctl status cabinet-api         │"
echo "  │  Logs:     journalctl -u cabinet-api -f         │"
echo "  │  Dashboard: http://localhost:$PORT/              │"
echo "  │  API Docs: http://localhost:$PORT/swagger        │"
echo "  ├─────────────────────────────────────────────────┤"
echo "  │  Config:   $CONFIG_DIR/appsettings.json"
echo "  │  Secrets:  $CONFIG_DIR/cabinet.env"
echo "  │  Data:     $DATA_DIR/"
echo "  │  Binaries: $INSTALL_DIR/"
echo "  └─────────────────────────────────────────────────┘"
echo ""

if [[ "$BINARY_FOUND" = false ]]; then
    echo "  IMPORTANT: Copy the Cabinet.API binary to $INSTALL_DIR/api/ before starting."
    echo ""
fi

# Run wizard if requested
if [[ "$RUN_WIZARD" = true ]] && [[ -f "$INSTALL_DIR/cli/cabinet" ]]; then
    echo "  Launching setup wizard..."
    "$INSTALL_DIR/cli/cabinet" setup
fi
