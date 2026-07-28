#!/usr/bin/env bash
set -euo pipefail

AUTH_CONF=/opt/arthas/server/etc/authserver.conf
USERNAME_FILE=/tmp/wotlk-launcher-test-user

if [ ! -s "$USERNAME_FILE" ]; then
  exit 0
fi

username=$(cat "$USERNAME_FILE")
dbinfo=$(sudo awk -F'"' '/^[[:space:]]*LoginDatabaseInfo[[:space:]]*=/{print $2; exit}' "$AUTH_CONF")
IFS=';' read -r dbhost dbport dbuser dbpass dbname <<< "$dbinfo"

sql="SET @aid=(SELECT id FROM account WHERE username='${username}' LIMIT 1);
DELETE FROM realmcharacters WHERE acctid=@aid;
DELETE FROM atlas_launcher_session WHERE account_id=@aid;
DELETE FROM atlas_launcher_profile WHERE account_id=@aid;
DELETE FROM hermes_bnet_credentials WHERE username='${username}';
DELETE FROM account WHERE id=@aid;"

sudo docker exec arthas-mysql \
  mysql --silent --skip-column-names \
  -u"$dbuser" "-p$dbpass" "$dbname" \
  -e "$sql" >/dev/null 2>&1
rm -f "$USERNAME_FILE"
echo "smoke test account removed"
