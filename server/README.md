# WotLK Launcher Server

Serveur prive pour le launcher WotLK.

Il sert:
- `GET /health`
- `GET /wotlk/manifest.json`
- `GET /wotlk/files/<path>`

Les routes `/wotlk/*` exigent:

~~~http
Authorization: Bearer <token>
~~~

## Variables

- `PORT`: port local, defaut `4322`
- `WOTLK_FEED_ROOT`: dossier du feed, defaut `/srv/wotlk/launcher-feed`
- `WOTLK_LAUNCHER_TOKEN`: token prive obligatoire
- `WOTLK_PUBLIC_BASE_URL`: URL publique utilisee dans le manifeste, defaut `http://152.228.225.7/wotlk/`

## Exemple systemd

~~~ini
[Unit]
Description=WotLK launcher private feed
After=network.target

[Service]
User=debian
WorkingDirectory=/opt/wotlk-launcher-server
Environment=PORT=4322
Environment=WOTLK_FEED_ROOT=/srv/wotlk/launcher-feed
Environment=WOTLK_PUBLIC_BASE_URL=http://152.228.225.7/wotlk/
EnvironmentFile=/etc/wotlk/launcher.env
ExecStart=/usr/bin/node /opt/wotlk-launcher-server/src/server.js
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
~~~

## Exemple Caddy via IP

~~~caddyfile
http://152.228.225.7 {
    encode zstd gzip

    handle /wotlk/* {
        reverse_proxy 127.0.0.1:4322 {
            header_up Authorization "Bearer {env.WOTLK_LAUNCHER_TOKEN}"
        }
    }
}
~~~

Le feed reste hors du repertoire web public. Le token reste cote serveur: Caddy l'ajoute entre le proxy public et le service local.
