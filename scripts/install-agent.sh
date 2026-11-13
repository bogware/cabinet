#!/usr/bin/env bash
# Cabinet agent installer for Linux (systemd).
#
# Run as root from the folder you extracted cabinet-linux-x64.tar.gz into (it finds
# agent/cabinet-agent there), or pass --binary.
#
#   ./install-agent.sh --server https://cabinet:8443 --pin FINGERPRINT --api-key KEY --paths "/var/log/*.log"
#   ./install-agent.sh --config my-agent.json --groups game1,game2
#
# --config installs a complete appsettings.json instead of generating one, for several inputs.
# --groups adds the service user to groups, to read logs in other users' folders.
# --writable lists folders the service may change, for inputs with DeleteDeliveredAfterDays.
# Running it again upgrades the binary and keeps the settings and checkpoints.

set -euo pipefail

INSTALL_DIR="/opt/cabinet-agent"
SERVICE_USER="cabinet-agent"
SERVER_URL=""
API_KEY=""
LOG_PATHS=""
SOURCE_TYPE="generic"
INDEX="main"
CONFIG_FILE=""
BINARY=""
PIN=""
GROUPS_CSV=""
WRITABLE_CSV=""
START=true

usage() {
    sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
    echo "Options: --server URL --pin SHA256 --api-key KEY --paths \"a,b\" --source-type T --index I"
    echo "         --config FILE --binary PATH --groups g1,g2 --writable d1,d2 --install-dir DIR --no-start"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --server) SERVER_URL="$2"; shift 2 ;;
        --api-key) API_KEY="$2"; shift 2 ;;
        --pin) PIN="$2"; shift 2 ;;
        --paths) LOG_PATHS="$2"; shift 2 ;;
        --source-type) SOURCE_TYPE="$2"; shift 2 ;;
        --index) INDEX="$2"; shift 2 ;;
        --config) CONFIG_FILE="$2"; shift 2 ;;
        --binary) BINARY="$2"; shift 2 ;;
        --groups) GROUPS_CSV="$2"; shift 2 ;;
        --writable) WRITABLE_CSV="$2"; shift 2 ;;
        --install-dir) INSTALL_DIR="$2"; shift 2 ;;
        --no-start) START=false; shift ;;
        --help|-h) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

if [[ $EUID -ne 0 ]]; then
    echo "Run this as root (or with sudo)." >&2
    exit 1
fi

if [[ "$(uname -s)" != "Linux" ]]; then
    echo "This installer is for Linux." >&2
    exit 1
fi

# The binary: given, or beside this script as the release archive lays it out
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -z "$BINARY" ]]; then
    for candidate in "$PWD/agent/cabinet-agent" "$here/agent/cabinet-agent" "$here/../agent/cabinet-agent" "$PWD/cabinet-agent"; do
        if [[ -f "$candidate" ]]; then BINARY="$candidate"; break; fi
    done
fi
if [[ -z "$BINARY" || ! -f "$BINARY" ]]; then
    echo "cabinet-agent not found. Extract the release archive and run this from that folder, or pass --binary." >&2
    exit 1
fi

echo "Cabinet agent installer"
echo "  Binary:  $BINARY"
echo "  Install: $INSTALL_DIR"

if ! id "$SERVICE_USER" &>/dev/null; then
    useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

if [[ -n "$GROUPS_CSV" ]]; then
    usermod -a -G "$GROUPS_CSV" "$SERVICE_USER"
    echo "  Groups:  $GROUPS_CSV"
fi

# Folders the agent deletes delivered logs from: writable through the sandbox. Kept from the
# previous install unless given again.
writable_paths=""
if [[ -n "$WRITABLE_CSV" ]]; then
    writable_paths=$(echo "$WRITABLE_CSV" | tr ',' ' ')
elif [[ -f /etc/systemd/system/cabinet-agent.service ]]; then
    writable_paths=$(sed -n 's/^# Writable: //p' /etc/systemd/system/cabinet-agent.service)
fi

systemctl stop cabinet-agent 2>/dev/null || true
mkdir -p "$INSTALL_DIR"/{spool,data}
install -m 755 "$BINARY" "$INSTALL_DIR/cabinet-agent"

# Settings are kept on upgrade, unless a new --config is given
if [[ -n "$CONFIG_FILE" ]]; then
    install -m 600 "$CONFIG_FILE" "$INSTALL_DIR/appsettings.json"
    echo "  Settings from $CONFIG_FILE"
elif [[ ! -f "$INSTALL_DIR/appsettings.json" ]]; then
    if [[ -z "$SERVER_URL" || -z "$API_KEY" ]]; then
        echo "Give --server and --api-key, or --config." >&2
        exit 1
    fi
    paths_json='"/var/log/syslog", "/var/log/auth.log"'
    if [[ -n "$LOG_PATHS" ]]; then
        paths_json=$(echo "$LOG_PATHS" | tr ',' '\n' | sed 's/^ *//;s/ *$//;s/^/"/;s/$/"/' | paste -sd, -)
    fi
    cat > "$INSTALL_DIR/appsettings.json" <<EOF
{
  "Agent": {
    "Inputs": [
      {
        "Paths": [$paths_json],
        "SourceType": "$SOURCE_TYPE",
        "Index": "$INDEX",
        "ExcludePatterns": ["*.gz", "*.zip", "*.bz2"],
        "IgnoreOlderThanHours": 24
      }
    ],
    "Output": {
      "ServerUrl": "$SERVER_URL",
      "ApiKey": "$API_KEY",
      "CertificateSha256": "$PIN",
      "SpillDirectory": "$INSTALL_DIR/spool"
    },
    "Registry": {
      "Path": "$INSTALL_DIR/data/registry.json"
    }
  }
}
EOF
    echo "  Settings written to $INSTALL_DIR/appsettings.json"
fi

# It holds the API key
chmod 600 "$INSTALL_DIR/appsettings.json"
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"
# Spooled events are copies of logs that may be root-only, such as auth.log
chmod 700 "$INSTALL_DIR/spool" "$INSTALL_DIR/data"

cat > /etc/systemd/system/cabinet-agent.service <<EOF
[Unit]
Description=Cabinet log forwarding agent
Documentation=https://github.com/bogware/cabinet
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=$INSTALL_DIR/cabinet-agent
WorkingDirectory=$INSTALL_DIR
Restart=always
RestartSec=5
TimeoutStopSec=30

User=$SERVICE_USER
Group=$SERVICE_USER
# Spool and dead-letter files hold copies of logs: readable by the agent only
UMask=0077
NoNewPrivileges=true
ProtectSystem=strict
# Logs often live in home folders. Readable, never writable.
ProtectHome=read-only
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
ReadWritePaths=$INSTALL_DIR $writable_paths
# Writable: $writable_paths
# Reading logs is light; never take CPU from what's being logged
Nice=10
CPUWeight=20

Environment=DOTNET_ENVIRONMENT=Production

StandardOutput=journal
StandardError=journal
SyslogIdentifier=cabinet-agent

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable cabinet-agent >/dev/null

if $START; then
    systemctl restart cabinet-agent
    sleep 3
    systemctl --no-pager --lines=0 status cabinet-agent | head -3
fi

echo ""
echo "Done. Follow it with: journalctl -u cabinet-agent -f"
