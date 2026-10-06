#!/bin/sh
# Copies published sites and uploads to server 2 so a failover has recent files.
# Skips while server 2 is the active origin (those files would be newer).
# crontab: */5 * * * * /opt/casco/deploy/server1/sync-data.sh >> /var/log/casco-sync.log 2>&1
set -eu

SERVER2="${SERVER2:-root@86.48.1.150}"
SSH_KEY="${SSH_KEY:-/root/.ssh/casco_backup}"
SSH_OPTS="-i $SSH_KEY -o StrictHostKeyChecking=accept-new"
DATA_DIR="$(dirname "$0")/data"

origin=$(ssh $SSH_OPTS "$SERVER2" "cd /opt/casco/deploy/server2 && docker compose exec -T db psql -U casco -d casco -tAc \"SELECT active_server FROM cluster_state WHERE id=1\"" || true)
origin=$(printf '%s' "$origin" | tr -d '[:space:]')
if [ "$origin" = "server2" ]; then
  echo "$(date -Is) skip: server 2 is active"
  exit 0
fi

ssh $SSH_OPTS "$SERVER2" "mkdir -p /opt/casco/deploy/server2/data/sites /opt/casco/deploy/server2/data/uploads && chown -R 1654:1654 /opt/casco/deploy/server2/data"
if [ -d "$DATA_DIR/sites" ]; then
  rsync -a --delete -e "ssh $SSH_OPTS" "$DATA_DIR/sites/" "$SERVER2:/opt/casco/deploy/server2/data/sites/"
fi
if [ -d "$DATA_DIR/uploads" ]; then
  rsync -a --delete -e "ssh $SSH_OPTS" "$DATA_DIR/uploads/" "$SERVER2:/opt/casco/deploy/server2/data/uploads/"
fi
echo "$(date -Is) sync ok"
