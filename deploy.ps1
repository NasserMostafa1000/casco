# From the Casco folder, one command:
#   powershell -ExecutionPolicy Bypass -File .\deploy.ps1
# Builds frontend\dist (upload that folder to Cloudflare Pages), then updates the API
# on server 1 and the standby copy on the database server.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$key = Join-Path $env:USERPROFILE '.ssh\casco_deploy'
if (-not (Test-Path $key)) { throw "SSH key not found: $key" }

$server1 = '86.48.5.19'
$server2 = '86.48.1.150'

function Invoke-Casco([string]$Target, [string]$Script) {
    $Script = ($Script -replace "`r", "").Trim() + "`n"
    $sh = Join-Path $env:TEMP ("casco-remote-" + $Target.Replace('.', '-') + ".sh")
    [IO.File]::WriteAllText($sh, $Script, [Text.UTF8Encoding]::new($false))
    cmd /c "ssh.exe -i `"$key`" -o StrictHostKeyChecking=accept-new -o ServerAliveInterval=30 root@$Target bash -s < `"$sh`""
    if ($LASTEXITCODE -ne 0) { throw "Remote command failed on $Target" }
}

Write-Host '1/4 Building the frontend...'
Push-Location (Join-Path $PSScriptRoot 'frontend')
& npm.cmd run build
if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
Pop-Location

Write-Host '2/4 Packing the code...'
$tgz = Join-Path $env:TEMP 'casco-code.tgz'
if (Test-Path $tgz) { Remove-Item $tgz -Force }
& tar.exe -czf $tgz `
  --exclude=node_modules --exclude=dist --exclude=bin --exclude=obj `
  --exclude=data --exclude=TestResults --exclude=.env --exclude=.env.api --exclude=failover.env `
  --exclude=appsettings.Development.json --exclude=certs --exclude=.git `
  -C $PSScriptRoot backend frontend deploy
if ($LASTEXITCODE -ne 0) { throw 'Could not pack the code' }

$paused = $false
try {
    Write-Host '3/4 Uploading to both servers...'
    Invoke-Casco $server2 @'
mkdir -p /var/lib/casco /opt/casco
touch /var/lib/casco/failover.pause
'@
    $paused = $true

    & scp.exe -i $key -o StrictHostKeyChecking=accept-new $tgz "root@${server1}:/tmp/casco-code.tgz"
    if ($LASTEXITCODE -ne 0) { throw 'Upload to server 1 failed' }
    & scp.exe -i $key -o StrictHostKeyChecking=accept-new $tgz "root@${server2}:/tmp/casco-code.tgz"
    if ($LASTEXITCODE -ne 0) { throw 'Upload to server 2 failed' }

    $extract = @'
set -eu
mkdir -p /opt/casco
cd /opt/casco
tar -xzf /tmp/casco-code.tgz
rm -f /tmp/casco-code.tgz
find /opt/casco/deploy \( -name '*.sh' -o -name '*.py' \) -exec sed -i 's/\r$//' {} +
'@
    Invoke-Casco $server1 $extract
    Invoke-Casco $server2 $extract

    Write-Host '4/4 Updating server 1, then the standby on server 2...'
    Invoke-Casco $server1 @'
set -euo pipefail
python3 /opt/casco/deploy/server2/import-env.py server1
cd /opt/casco/deploy/server1
docker compose build api </dev/null
docker compose up -d
ok=0
for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24; do
  if curl -sk --resolve api.casco.studio:443:127.0.0.1 https://api.casco.studio/health | grep -q '"status":"ok"'; then
    ok=1
    break
  fi
  sleep 5
done
if [ "$ok" != 1 ]; then
  echo HEALTH_FAILED
  docker compose logs --tail 40 api
  exit 1
fi
echo SERVER1_OK

KEY=/root/.ssh/casco_backup
S2=root@86.48.1.150
# -n so these ssh calls do not swallow the rest of this script (it is itself on stdin).
SSH="ssh -n -i $KEY -o StrictHostKeyChecking=accept-new"
$SSH "$S2" "mkdir -p /opt/casco/deploy/server2/certs /opt/casco/deploy/server2/data/sites /opt/casco/deploy/server2/data/uploads"
scp -i "$KEY" -o StrictHostKeyChecking=accept-new \
  /opt/casco/deploy/server1/certs/origin.pem \
  /opt/casco/deploy/server1/certs/origin.key \
  "$S2:/opt/casco/deploy/server2/certs/" </dev/null
scp -i "$KEY" -o StrictHostKeyChecking=accept-new \
  /opt/casco/deploy/server1/.env "$S2:/tmp/casco-s1.env" </dev/null
$SSH "$S2" "python3 /opt/casco/deploy/server2/import-env.py server2"
echo LOADING_IMAGE
docker save casco-api:latest | ssh -i "$KEY" -o StrictHostKeyChecking=accept-new "$S2" docker load

cat > /tmp/casco-s2.sh << 'EOS'
set -euo pipefail
if ! command -v rsync >/dev/null || ! command -v python3 >/dev/null; then
  apt-get update
  apt-get install -y rsync python3
fi
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 443/udp
chown -R 1654:1654 /opt/casco/deploy/server2/data
cd /opt/casco/deploy/server2
docker compose up -d
ok=0
for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24; do
  if curl -sk --resolve api.casco.studio:443:127.0.0.1 https://api.casco.studio/health | grep -q '"status":"ok"'; then
    ok=1
    break
  fi
  sleep 5
done
if [ "$ok" != 1 ]; then
  echo STANDBY_HEALTH_FAILED
  docker compose ps
  docker compose logs --tail 50 api
  exit 1
fi
cp /opt/casco/deploy/server2/casco-failover.service /etc/systemd/system/casco-failover.service
systemctl daemon-reload
systemctl enable casco-failover
systemctl restart casco-failover
if [ ! -f /root/.ssh/casco_sync ]; then
  ssh-keygen -t ed25519 -f /root/.ssh/casco_sync -N "" -q
fi
echo SERVER2_OK
EOS
ssh -i "$KEY" -o StrictHostKeyChecking=accept-new "$S2" bash -s < /tmp/casco-s2.sh

touch /root/.ssh/authorized_keys
chmod 600 /root/.ssh/authorized_keys
pub=$($SSH "$S2" "cat /root/.ssh/casco_sync.pub")
grep -qF "$pub" /root/.ssh/authorized_keys || echo "from=\"86.48.1.150\" $pub" >> /root/.ssh/authorized_keys
if ! command -v rsync >/dev/null; then
  apt-get update
  apt-get install -y rsync
fi
chmod +x /opt/casco/deploy/server1/sync-data.sh /opt/casco/deploy/server1/backup-data.sh
line='*/2 * * * * /opt/casco/deploy/server1/sync-data.sh >> /var/log/casco-sync.log 2>&1'
(crontab -l 2>/dev/null | grep -v sync-data.sh || true; echo "$line") | crontab -
/opt/casco/deploy/server1/sync-data.sh
echo DEPLOY_OK
'@
}
finally {
    if ($paused) {
        try {
            Invoke-Casco $server2 @'
rm -f /var/lib/casco/failover.pause
systemctl try-restart casco-failover || true
'@
        }
        catch {
            Write-Host "Could not clear the failover pause on server 2. Remove /var/lib/casco/failover.pause or the standby will not take over."
        }
    }
}

Write-Host ''
Write-Host 'Both APIs are up. Upload this folder to Cloudflare Pages:'
Write-Host (Join-Path $PSScriptRoot 'frontend\dist')
