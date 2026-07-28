#!/usr/bin/env python3
import json
import secrets
import sys
import time
import urllib.error
import urllib.request


base_url = sys.argv[1].rstrip("/") + "/"
username_file = sys.argv[2]
username = "CODEXTEST" + str(int(time.time()))
password = "Atlas!" + secrets.token_urlsafe(18)
email = username.lower() + "@example.invalid"


def request(path, method="GET", body=None, token=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(
        base_url + path,
        data=data,
        headers=headers,
        method=method,
    )
    try:
        with urllib.request.urlopen(req, timeout=20) as response:
            payload = response.read()
            return response.status, json.loads(payload) if payload else None
    except urllib.error.HTTPError as error:
        return error.code, None


with open(username_file, "w", encoding="ascii") as output:
    output.write(username)

status, auth = request(
    "api/v1/accounts",
    "POST",
    {"username": username, "email": email, "password": password},
)
assert status == 200, ("register", status)
assert auth["profile"]["completion"] == 40

status, profile = request(
    "api/v1/me",
    token=auth["accessToken"],
)
assert status == 200, ("profile", status)
assert profile["username"] == username

status, refreshed = request(
    "api/v1/auth/refresh",
    "POST",
    {"refreshToken": auth["refreshToken"]},
)
assert status == 200, ("refresh", status)
assert refreshed["accessToken"] != auth["accessToken"]

status, _ = request("manifest.json")
assert status == 401, ("anonymous manifest", status)

status, manifest = request(
    "manifest.json",
    token=refreshed["accessToken"],
)
assert status == 200, ("authorized manifest", status)
assert manifest["baseUrl"] == "https://animeclub.fr/wotlk/files/"
assert len(manifest["files"]) > 0

status, logged_in = request(
    "api/v1/auth/login",
    "POST",
    {"username": username, "password": password, "deviceName": "smoke-test"},
)
assert status == 200, ("login", status)
assert logged_in["profile"]["username"] == username

status, avatar_profile = request(
    "api/v1/me/avatar",
    "PATCH",
    {"avatarKey": "ice"},
    logged_in["accessToken"],
)
assert status == 200, ("avatar", status)
assert avatar_profile["avatarKey"] == "ice"

status, sessions = request(
    "api/v1/me/sessions",
    token=logged_in["accessToken"],
)
assert status == 200, ("sessions", status)
assert any(session["current"] for session in sessions)

status, server_status = request(
    "api/v1/status",
    token=logged_in["accessToken"],
)
assert status == 200, ("status", status)
assert server_status["realm"] == "Arthas"
assert server_status["api"] is True

status, news = request(
    "api/v1/news",
    token=logged_in["accessToken"],
)
assert status == 200, ("news", status)
assert len(news) >= 3

new_password = password + "!updated"
status, _ = request(
    "api/v1/me/password",
    "POST",
    {"currentPassword": password, "newPassword": new_password},
    logged_in["accessToken"],
)
assert status == 204, ("password", status)

status, logged_in = request(
    "api/v1/auth/login",
    "POST",
    {"username": username, "password": new_password, "deviceName": "smoke-test-updated"},
)
assert status == 200, ("login with changed password", status)

status, game_ticket = request(
    "api/v1/game-ticket",
    "POST",
    {},
    logged_in["accessToken"],
)
assert status == 200, ("game ticket", status)
assert game_ticket["ticket"].startswith("ATL-")

print(
    "register=200 profile=200 refresh=200 anonymous_manifest=401 manifest=200 "
    "login=200 avatar=200 sessions=200 status=200 news=200 password=204 "
    "login_updated=200 game_ticket=200"
)
