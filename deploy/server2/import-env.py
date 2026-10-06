#!/usr/bin/env python3
"""Builds server 2's API env from server 1's .env, and marks server 1 as server1.

Never prints secret values. Usage on the matching machine:
  python3 import-env.py server1
  python3 import-env.py server2
"""
import os
import re
import stat
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
SERVER1_ENV = "/opt/casco/deploy/server1/.env"
STAGED = "/tmp/casco-s1.env"
API_ENV = os.path.join(ROOT, ".env.api")
FAILOVER_ENV = os.path.join(ROOT, "failover.env")
ZONE_ID = "6911267dc395d7a5b3dd22294efcf969"
SERVER2_IP = "86.48.1.150"


def read_env(path):
    with open(path, encoding="utf-8") as f:
        text = f.read().replace("\r\n", "\n").replace("\r", "\n")
    values = {}
    for line in text.split("\n"):
        if not line or line.lstrip().startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key] = value
    return text, values


def upsert(text, key, value):
    pattern = re.compile(rf"^{re.escape(key)}=.*$", re.M)
    line = f"{key}={value}"
    if pattern.search(text):
        return pattern.sub(line, text, count=1)
    if text and not text.endswith("\n"):
        text += "\n"
    return text + line + "\n"


def rewrite_connection(value):
    parts = value.split(";")
    out = []
    has_ssl = False
    for part in parts:
        name = part.strip().split("=", 1)[0].lower()
        if name == "host":
            out.append("Host=db")
        elif name == "ssl mode":
            out.append("SSL Mode=Disable")
            has_ssl = True
        else:
            out.append(part)
    if not has_ssl:
        out.append("SSL Mode=Disable")
    return ";".join(out)


def write_secret(path, text):
    tmp = path + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, stat.S_IRUSR | stat.S_IWUSR)
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    os.replace(tmp, path)


def ensure_server1():
    text, _ = read_env(SERVER1_ENV)
    updated = upsert(text, "App__ServerId", "server1")
    if not re.search(r"^App__ServerIps__1=", updated, re.M):
        updated = upsert(updated, "App__ServerIps__1", SERVER2_IP)
    if updated != text:
        write_secret(SERVER1_ENV, updated)
        print("server1 env: set App__ServerId")
    else:
        print("server1 env: already marked")


def load_failover_token():
    if not os.path.exists(FAILOVER_ENV):
        return ""
    _, values = read_env(FAILOVER_ENV)
    return values.get("CLOUDFLARE_API_TOKEN", "")


def ensure_server2():
    if not os.path.exists(STAGED):
        sys.exit("missing /tmp/casco-s1.env")
    text, values = read_env(STAGED)
    if "ConnectionStrings__Default" not in values:
        sys.exit("server 1 env has no ConnectionStrings__Default")
    text = upsert(text, "ConnectionStrings__Default", rewrite_connection(values["ConnectionStrings__Default"]))
    text = upsert(text, "App__ServerId", "server2")
    if not re.search(r"^App__ServerIps__1=", text, re.M):
        text = upsert(text, "App__ServerIps__1", SERVER2_IP)
    write_secret(API_ENV, text)
    os.remove(STAGED)

    token = values.get("CLOUDFLARE_API_TOKEN") or values.get("Cloudflare__ApiToken") or load_failover_token()
    alert = values.get("Monitor__AlertEmails__0", "")
    lines = [
        f"SMTP_HOST={values.get('Smtp__Host', '')}",
        f"SMTP_PORT={values.get('Smtp__Port', '587')}",
        f"SMTP_USER={values.get('Smtp__User', '')}",
        f"SMTP_PASSWORD={values.get('Smtp__Password', '')}",
        f"SMTP_FROM={values.get('Smtp__From', 'Casco <no-reply@casco.studio>')}",
        f"SMTP_SSL={values.get('Smtp__EnableSsl', 'true')}",
        f"ALERT_TO={alert}",
        f"CLOUDFLARE_API_TOKEN={token}",
        f"CLOUDFLARE_ZONE_ID={values.get('CLOUDFLARE_ZONE_ID', ZONE_ID)}",
        "PRIMARY_IP=86.48.5.19",
        f"STANDBY_IP={SERVER2_IP}",
        "HEALTH_NAME=api.casco.studio",
        "",
    ]
    write_secret(FAILOVER_ENV, "\n".join(lines))
    print("server2 env: api env ready; cloudflare token " + ("set" if token else "MISSING"))


if __name__ == "__main__":
    if len(sys.argv) != 2 or sys.argv[1] not in ("server1", "server2"):
        sys.exit("usage: import-env.py server1|server2")
    if sys.argv[1] == "server1":
        ensure_server1()
    else:
        ensure_server2()
