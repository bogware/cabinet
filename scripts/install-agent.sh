#!/usr/bin/env bash
# Cabinet Agent Installer
# Usage: curl -sSL https://your-server/install-agent.sh | bash
# Or: ./install-agent.sh [--server URL] [--api-key KEY] [--paths "/var/log/*.log"]

set -euo pipefail

INSTALL_DIR="/opt/cabinet-agent"
SERVICE_USER="cabinet-agent"
SERVER_URL=""
API_KEY=""
LOG_PATHS=""

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --server)
            SERVER_URL="$2"
            shift 2
            ;;
        --api-key)
            API_KEY="$2"
            shift 2
            ;;
        --paths)
            LOG_PATHS="$2"
            shift 2
            ;;
        --install-dir)
            INSTALL_DIR="$2"
            shift 2
            ;;
        --help)
            echo "Usage: $0 [--server URL] [--api-key KEY] [--paths \"/var/log/*.log\"] [--install-dir DIR]"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

# Detect OS and architecture
OS=$(uname -s | tr '[:upper:]' '[:lower:]')
ARCH=$(uname -m)

case "$ARCH" in
    x86_64)  RID="${OS}-x64" ;;
    aarch64) RID="${OS}-arm64" ;;
    arm64)   RID="${OS}-arm64" ;;
    *)
        echo "Unsupported architecture: $ARCH"
        exit 1
        ;;
esac

case "$OS" in
    linux)  RID="linux-${RID#linux-}" ;;
    darwin) RID="osx-${RID#darwin-}" ;;
    *)
        echo "Unsupported OS: $OS (use Linux or macOS)"
        exit 1
        ;;
esac

echo "Cabinet Agent Installer"
echo "======================"
echo "OS/Arch: $RID"
echo "Install dir: $INSTALL_DIR"
echo ""

# Check for root
if [[ $EUID -ne 0 ]]; then
    echo "This installer must be run as root (or with sudo)."
    exit 1
fi

# Create service user
if ! id "$SERVICE_USER" &>/dev/null; then
    echo "Creating service user: $SERVICE_USER"
    useradd --system --no-create-home --shell /sbin/nologin "$SERVICE_USER"
fi

# Create install directory
echo "Creating install directory: $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"/{spool,data}

# Check if binary already exists (upgrade scenario)
if [[ -f "$INSTALL_DIR/cabinet-agent" ]]; then
    echo "Existing installation found. Stopping service..."
    systemctl stop cabinet-agent 2>/dev/null || true
fi

echo ""
echo "NOTE: Copy the cabinet-agent binary for your platform ($RID) to $INSTALL_DIR/"
echo "      Download from: https://github.com/draxios/cabinet/releases"
echo ""

# Generate default configuration if not present
if [[ ! -f "$INSTALL_DIR/appsettings.json" ]]; then
    echo "Generating default configuration..."

    # Build paths JSON array
    if [[ -n "$LOG_PATHS" ]]; then
        PATHS_JSON=$(echo "$LOG_PATHS" | tr ',' '\n' | sed 's/^/      "/;s/$/"/' | paste -sd, -)
    else
        PATHS_JSON='"/var/log/syslog", "/var/log/auth.log"'
    fi

    cat > "$INSTALL_DIR/appsettings.json" <<EOF
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Cabinet.Agent": "Information"
    }
  },
  "Agent": {
    "Inputs": [
      {
        "Paths": [$PATHS_JSON],
        "Recursive": false,
        "SourceType": "syslog",
        "Index": "main",
        "ExcludePatterns": ["*.gz", "*.zip", "*.bz2"]
      }
    ],
    "Output": {
      "ServerUrl": "${SERVER_URL:-http://localhost:5000}",
      "ApiKey": "${API_KEY}",
      "BatchSize": 100,
      "FlushIntervalSeconds": 5,
      "MaxRetries": 5,
      "TimeoutSeconds": 30,
      "VerifySsl": true,
      "MaxBufferSize": 10000,
      "SpillDirectory": "$INSTALL_DIR/spool"
    },
    "Registry": {
      "Path": "$INSTALL_DIR/data/registry.json",
      "SaveIntervalSeconds": 10
    },
    "ScanIntervalSeconds": 10,
    "Hostname": ""
  }
}
EOF

    echo "Configuration written to $INSTALL_DIR/appsettings.json"
fi

# Create .env file for sensitive config
if [[ ! -f "$INSTALL_DIR/.env" ]]; then
    cat > "$INSTALL_DIR/.env" <<EOF
# Cabinet Agent environment variables
# Override configuration values here (e.g., API key)
# Agent__Output__ApiKey=your-api-key-here
EOF
    chmod 600 "$INSTALL_DIR/.env"
fi

# Set ownership
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"

# Install systemd service
if [[ -d /etc/systemd/system ]]; then
    echo "Installing systemd service..."
    cat > /etc/systemd/system/cabinet-agent.service <<SVCEOF
[Unit]
Description=Cabinet Log Forwarding Agent
Documentation=https://github.com/draxios/cabinet
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=$INSTALL_DIR/cabinet-agent
WorkingDirectory=$INSTALL_DIR
Restart=always
RestartSec=5
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
ReadWritePaths=$INSTALL_DIR /var/log

Environment=DOTNET_ENVIRONMENT=Production
EnvironmentFile=-$INSTALL_DIR/.env

StandardOutput=journal
StandardError=journal
SyslogIdentifier=cabinet-agent

[Install]
WantedBy=multi-user.target
SVCEOF

    systemctl daemon-reload
    echo "Systemd service installed."
    echo ""
    echo "To start:  systemctl start cabinet-agent"
    echo "To enable: systemctl enable cabinet-agent"
    echo "To check:  systemctl status cabinet-agent"
    echo "Logs:      journalctl -u cabinet-agent -f"
fi

echo ""
echo "Installation complete!"
echo ""
echo "Next steps:"
echo "  1. Copy the cabinet-agent binary to $INSTALL_DIR/"
echo "  2. Edit $INSTALL_DIR/appsettings.json (set ServerUrl and ApiKey)"
echo "  3. Start: systemctl start cabinet-agent"
echo "  4. Enable on boot: systemctl enable cabinet-agent"
