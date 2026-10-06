#!/usr/bin/env python3
"""Watches server 1 from the database server and fails over when it stops answering.

Traffic moves by updating the Cloudflare A records (api, *.casco.studio, sites).
Caddy on this server then receives the visitors. An e-mail is sent on each switch.
Deploy pauses this by creating /var/lib/casco/failover.pause.
"""
import json
import os
import smtplib
import ssl
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
from email.message import EmailMessage

ROOT = os.path.dirname(os.path.abspath(__file__))
ENV_PATH = os.path.join(ROOT, "failover.env")
PAUSE = "/var/lib/casco/failover.pause"
NOTE = "/var/lib/casco/failover-note"
COMPOSE = ["docker", "compose", "exec", "-T", "db", "psql", "-U", "casco", "-d", "casco", "-v", "ON_ERROR_STOP=1"]
FAILS_REQUIRED = 3
OKS_REQUIRED = 9
INTERVAL = 20
RECORDS = ("api.casco.studio", "*.casco.studio", "sites.casco.studio")


def log(message):
    print(time.strftime("%Y-%m-%dT%H:%M:%S%z"), message, flush=True)


def load_env():
    env = {}
    with open(ENV_PATH, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            env[key] = value
    return env


def psql(sql):
    return subprocess.run(COMPOSE + ["-c", sql], cwd=ROOT, check=True, capture_output=True, text=True).stdout


def ensure_table():
    psql("""
        CREATE TABLE IF NOT EXISTS cluster_state (
            id integer PRIMARY KEY,
            active_server text NOT NULL
        );
        INSERT INTO cluster_state (id, active_server) VALUES (1, 'server1')
        ON CONFLICT (id) DO NOTHING;
    """)


def active_server():
    out = subprocess.run(
        COMPOSE + ["-tAc", "SELECT active_server FROM cluster_state WHERE id = 1"],
        cwd=ROOT, check=True, capture_output=True, text=True,
    ).stdout
    return out.strip() or "server1"


def set_active(server):
    if server not in ("server1", "server2"):
        raise SystemExit("refusing unexpected server id")
    psql(f"UPDATE cluster_state SET active_server = '{server}' WHERE id = 1")


def health(ip, env):
    name = env.get("HEALTH_NAME", "api.casco.studio")
    result = subprocess.run(
        ["curl", "-kfsS", "--max-time", "8", "--resolve", f"{name}:443:{ip}", f"https://{name}/health"],
        capture_output=True, text=True,
    )
    return result.returncode == 0 and '"status":"ok"' in result.stdout, result.stdout


def noted(state):
    try:
        with open(NOTE, encoding="utf-8") as f:
            return f.read().strip() == state
    except OSError:
        return False


def mark(state):
    os.makedirs("/var/lib/casco", exist_ok=True)
    with open(NOTE, "w", encoding="utf-8") as f:
        f.write(state)


def send_mail(env, subject, body):
    host = env.get("SMTP_HOST", "")
    user = env.get("SMTP_USER", "")
    password = env.get("SMTP_PASSWORD", "")
    recipients = [item.strip() for item in env.get("ALERT_TO", "").split(",") if item.strip()]
    if not host or not user or not password or not recipients:
        log("email not sent: SMTP or recipient is missing")
        return
    message = EmailMessage()
    message["Subject"] = subject
    message["From"] = env.get("SMTP_FROM") or user
    message["To"] = ", ".join(recipients)
    message.set_content(body)
    try:
        with smtplib.SMTP(host, int(env.get("SMTP_PORT") or "587"), timeout=30) as smtp:
            if env.get("SMTP_SSL", "true").lower() != "false":
                smtp.starttls(context=ssl.create_default_context())
            smtp.login(user, password)
            smtp.send_message(message)
        log("email sent: " + subject)
    except Exception as ex:
        log("email failed: " + str(ex))


def cf_request(env, method, path, payload=None):
    token = env.get("CLOUDFLARE_API_TOKEN", "")
    if not token:
        raise RuntimeError("CLOUDFLARE_API_TOKEN is empty")
    data = None if payload is None else json.dumps(payload).encode()
    zone = env.get("CLOUDFLARE_ZONE_ID", "")
    request = urllib.request.Request(
        f"https://api.cloudflare.com/client/v4/zones/{zone}{path}",
        data=data,
        method=method,
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        body = json.loads(response.read().decode())
    if not body.get("success"):
        raise RuntimeError(json.dumps(body.get("errors"), ensure_ascii=False))
    return body


def record_ips(env):
    found = {}
    for name in RECORDS:
        query = urllib.parse.urlencode({"type": "A", "name": name})
        body = cf_request(env, "GET", "/dns_records?" + query)
        result = body.get("result") or []
        if not result:
            raise RuntimeError("DNS record not found: " + name)
        found[name] = (result[0]["id"], result[0]["content"])
    return found


def point_dns(env, ip):
    current = record_ips(env)
    changed = []
    for name, (record_id, content) in current.items():
        if content == ip:
            continue
        cf_request(env, "PATCH", f"/dns_records/{record_id}", {"content": ip})
        changed.append(name)
    return changed


def dns_is(env, ip):
    try:
        current = record_ips(env)
    except Exception as ex:
        log("dns check failed: " + str(ex))
        return False
    return all(content == ip for _, content in current.values())


def rsync_from_primary(env):
    key = "/root/.ssh/casco_sync"
    ssh = f"ssh -i {key} -o StrictHostKeyChecking=accept-new"
    src = f"root@{env['PRIMARY_IP']}:/opt/casco/deploy/server1/data"
    for folder in ("sites", "uploads"):
        dest = os.path.join(ROOT, "data", folder)
        os.makedirs(dest, exist_ok=True)
        subprocess.run(
            ["rsync", "-a", "--delete", "-e", ssh, f"{src}/{folder}/", dest + "/"],
            check=True,
        )


def rsync_back(env):
    key = "/root/.ssh/casco_sync"
    ssh = f"ssh -i {key} -o StrictHostKeyChecking=accept-new"
    dest = f"root@{env['PRIMARY_IP']}:/opt/casco/deploy/server1/data"
    for folder in ("sites", "uploads"):
        src = os.path.join(ROOT, "data", folder)
        if not os.path.isdir(src):
            continue
        subprocess.run(
            ["rsync", "-a", "--delete", "-e", ssh, src + "/", f"{dest}/{folder}/"],
            check=True,
        )


def wait_leader(env, ip):
    for _ in range(18):
        ok, body = health(ip, env)
        if ok and '"leader":true' in body.replace(" ", ""):
            return True
        time.sleep(5)
    return False


def main():
    os.makedirs("/var/lib/casco", exist_ok=True)
    fails = 0
    oks = 0
    while True:
        time.sleep(INTERVAL)
        try:
            env = load_env()
            if os.path.exists(PAUSE):
                fails = 0
                continue
            ensure_table()
            origin = active_server()
            primary_ok, _ = health(env["PRIMARY_IP"], env)
            if primary_ok:
                fails = 0
                oks += 1
                if origin == "server2" and oks >= OKS_REQUIRED:
                    log("server 1 is back; returning traffic")
                    try:
                        rsync_back(env)
                    except Exception as ex:
                        log("failback paused, data copy failed: " + str(ex))
                        if not noted("failback-blocked"):
                            send_mail(env, "كاسكو: السيرفر 1 رجع لكن التحويل متأخر",
                                      "السيرفر الأول بقى يرد، لكن نسخ ملفات المواقع راجعة له فشل.\n"
                                      "الزوار لسه على سيرفر قاعدة البيانات لحد ما النسخ ينجح.\n" + str(ex))
                            mark("failback-blocked")
                        oks = OKS_REQUIRED
                        continue
                    set_active("server1")
                    leader = wait_leader(env, env["PRIMARY_IP"])
                    try:
                        point_dns(env, env["PRIMARY_IP"])
                        send_mail(env, "كاسكو: السيرفر 1 رجع والزوار رجعوا له",
                                  "السيرفر الأول (86.48.5.19) بقى سليم لمدة حوالي 3 دقائق.\n"
                                  "تم إرجاع api.casco.studio ومواقع العملاء له.\n"
                                  + ("شغل المهام رجع له." if leader else "شغل المهام في الخلفية لسه بيتأكد، راقب /health."))
                        mark("primary")
                    except Exception as ex:
                        log("dns failback failed: " + str(ex))
                        send_mail(env, "كاسكو: السيرفر 1 سليم لكن الدومين لسه على السيرفر 2",
                                  "ملفات المواقع اتنسخت، والمهام المفروض ترجع للسيرفر الأول، "
                                  "لكن تحديث DNS في Cloudflare فشل:\n" + str(ex))
                        mark("dns-failback-failed")
                    oks = 0
                continue

            oks = 0
            fails += 1
            if origin == "server2":
                if not dns_is(env, env["STANDBY_IP"]):
                    try:
                        point_dns(env, env["STANDBY_IP"])
                        if not noted("standby"):
                            send_mail(env, "كاسكو: الدومين اتظبط على سيرفر قاعدة البيانات",
                                      "السيرفر الأول لسه واقع. سجلات DNS اتحدثت إلى 86.48.1.150.")
                        mark("standby")
                    except Exception as ex:
                        log("dns retry failed: " + str(ex))
                        if not noted("dns-failed"):
                            send_mail(env, "كاسكو: السيرفر 1 توقف وتحديث الدومين فشل",
                                      "نسخة الـ API على سيرفر قاعدة البيانات شغالة، "
                                      "لكن تحديث DNS في Cloudflare فشل:\n" + str(ex))
                            mark("dns-failed")
                continue
            if fails < FAILS_REQUIRED:
                continue
            standby_ok, _ = health("127.0.0.1", env)
            if not standby_ok:
                log("server 1 is down and server 2 API is not healthy")
                if not noted("both-down"):
                    send_mail(env, "كاسكو: السيرفر 1 واقع والنسخة الاحتياطية مش جاهزة",
                              "السيرفر الأول (86.48.5.19) لا يرد على /health، "
                              "ونسخت الـ API على سيرفر قاعدة البيانات (86.48.1.150) مش جاهزة.\n"
                              "الدومين لسه على السيرفر الأول. راجع الحاويات على السيرفر 2: docker compose ps")
                    mark("both-down")
                fails = 0
                continue
            log("failing over to server 2")
            try:
                rsync_from_primary(env)
            except Exception as ex:
                log("could not copy fresh files from server 1: " + str(ex))
            set_active("server2")
            leader = wait_leader(env, "127.0.0.1")
            try:
                point_dns(env, env["STANDBY_IP"])
                send_mail(env, "كاسكو: السيرفر 1 توقف والتحويل تم لسيرفر قاعدة البيانات",
                          "السيرفر الأول (86.48.5.19) توقف عن الرد.\n"
                          "الزوار اتحولوا تلقائياً إلى السيرفر الثاني (86.48.1.150) "
                          "عبر api.casco.studio و *.casco.studio و sites.casco.studio.\n"
                          + ("المهام في الخلفية اشتغلت على السيرفر الثاني." if leader
                             else "الـ API بيرد، لكن تفعيل المهام في الخلفية اتأخر. راقب /health على السيرفر 2."))
                mark("standby")
            except Exception as ex:
                log("dns failover failed: " + str(ex))
                send_mail(env, "كاسكو: السيرفر 1 توقف وتحديث الدومين فشل",
                          "السيرفر الأول لا يرد، ونسخة الـ API على سيرفر قاعدة البيانات شغالة.\n"
                          "تحديث DNS في Cloudflare فشل، فالزوار لسه بيتوجهوا للسيرفر الأول:\n" + str(ex) +
                          "\nحط CLOUDFLARE_API_TOKEN في /opt/casco/deploy/server2/failover.env لو فاضل.")
                mark("dns-failed")
            fails = 0
        except Exception as ex:
            log("watch loop error: " + str(ex))


if __name__ == "__main__":
    main()
