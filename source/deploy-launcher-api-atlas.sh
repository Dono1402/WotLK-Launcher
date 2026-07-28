#!/usr/bin/env bash
set -euo pipefail

APP_DIR=/opt/wotlk-launcher-api
ARCHIVE=/tmp/wotlk-launcher-api.tar.gz
AUTH_CONF=/opt/arthas/server/etc/authserver.conf
ENV_DIR=/etc/wotlk
ENV_FILE=$ENV_DIR/launcher-api.env
INTERNAL_SECRET_FILE=/etc/atlas-wotlk-internal.secret

if [ ! -f "$ARCHIVE" ]; then
  echo "missing archive: $ARCHIVE" >&2
  exit 1
fi
if [ ! -f "$AUTH_CONF" ]; then
  echo "missing auth config: $AUTH_CONF" >&2
  exit 1
fi

if ! id wotlklauncher >/dev/null 2>&1; then
  useradd --system --home-dir "$APP_DIR" --shell /usr/sbin/nologin wotlklauncher
fi

dbinfo=$(awk -F'"' '/^[[:space:]]*LoginDatabaseInfo[[:space:]]*=/{print $2; exit}' "$AUTH_CONF")
IFS=';' read -r dbhost dbport dbuser dbpass dbname <<< "$dbinfo"
if [ -z "$dbhost" ] || [ -z "$dbport" ] || [ -z "$dbuser" ] || [ -z "$dbname" ]; then
  echo "invalid LoginDatabaseInfo" >&2
  exit 1
fi
conn="Server=${dbhost};Port=${dbport};User ID=${dbuser};Password=${dbpass};Database=${dbname};TreatTinyAsBoolean=false;Allow User Variables=true"

if [ ! -s "$INTERNAL_SECRET_FILE" ]; then
  openssl rand -hex 32 > "$INTERNAL_SECRET_FILE"
  chown root:root "$INTERNAL_SECRET_FILE"
  chmod 0600 "$INTERNAL_SECRET_FILE"
fi
internal_secret=$(cat "$INTERNAL_SECRET_FILE")

systemctl stop wotlk-launcher-api.service 2>/dev/null || true
rm -rf "$APP_DIR"
install -d -m 0755 -o wotlklauncher -g wotlklauncher "$APP_DIR"
tar -xzf "$ARCHIVE" -C "$APP_DIR"
chmod +x "$APP_DIR/WotLK.Launcher.Server"
chown -R wotlklauncher:wotlklauncher "$APP_DIR"

install -d -m 0755 "$ENV_DIR"
{
  printf "ASPNETCORE_URLS=http://127.0.0.1:4323\n"
  printf "DOTNET_ENVIRONMENT=Production\n"
  printf "WOTLK_LAUNCHER_DB='%s'\n" "$conn"
  printf "WOTLK_HERMES_SHARED_SECRET=%s\n" "$internal_secret"
} > "$ENV_FILE"
chown root:root "$ENV_FILE"
chmod 0600 "$ENV_FILE"

cat > /etc/systemd/system/wotlk-launcher-api.service <<'SERVICE'
[Unit]
Description=Atlas WotLK launcher accounts, sessions and private feed
After=network-online.target mysql.service mariadb.service hermesproxy-wotlk.service
Wants=network-online.target

[Service]
Type=simple
User=wotlklauncher
Group=wotlklauncher
WorkingDirectory=/opt/wotlk-launcher-api
EnvironmentFile=/etc/wotlk/launcher-api.env
ExecStart=/opt/wotlk-launcher-api/WotLK.Launcher.Server
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=/tmp

[Install]
WantedBy=multi-user.target
SERVICE

systemctl daemon-reload
systemctl enable wotlk-launcher-api.service >/dev/null
systemctl restart wotlk-launcher-api.service
sleep 2
systemctl --no-pager --full status wotlk-launcher-api.service | sed -n '1,18p'
