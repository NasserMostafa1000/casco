#!/usr/bin/env sh
# Nightly backup of published sites + uploads to server 2.
# crontab -e:  30 3 * * * /opt/casco/deploy/server1/backup-data.sh >> /var/log/casco-backup.log 2>&1
set -eu

SERVER2="${SERVER2:-root@86.48.1.150}"
# Dedicated key; on server 2 it is restricted with from="SERVER1_IP" in authorized_keys.
SSH_KEY="${SSH_KEY:-/root/.ssh/casco_backup}"
SSH_OPTS="-i $SSH_KEY -o StrictHostKeyChecking=accept-new"
DATA_DIR="$(dirname "$0")/data"
STAMP="$(date +%F)"
ARCHIVE="/tmp/casco-data-$STAMP.tar.gz"

tar -czf "$ARCHIVE" -C "$DATA_DIR" sites uploads 2>/dev/null || tar -czf "$ARCHIVE" -C "$DATA_DIR" .
ssh $SSH_OPTS "$SERVER2" "mkdir -p /opt/casco-backups/data"
scp -q $SSH_OPTS "$ARCHIVE" "$SERVER2:/opt/casco-backups/data/"
ssh $SSH_OPTS "$SERVER2" "find /opt/casco-backups/data -name 'casco-data-*.tar.gz' -mtime +14 -delete"
rm -f "$ARCHIVE"
echo "$(date -Is) backup ok: $ARCHIVE"
