#!/bin/sh
# Reports this server's CPU, memory, disk, Postgres status and backup age to the Casco API every minute.
# The API e-mails the owner when something becomes dangerous, or when these reports stop arriving.
# Inside the container /proc/stat and /proc/meminfo describe the whole host.

: "${MONITOR_URL:?set MONITOR_URL in .env}"
: "${MONITOR_TOKEN:?set MONITOR_TOKEN in .env}"
INTERVAL="${MONITOR_INTERVAL:-60}"

cpu_sample() { awk '/^cpu /{ idle=$5+$6; total=0; for (i=2; i<=9; i++) total+=$i; print idle, total }' /proc/stat; }

set -- $(cpu_sample); prev_idle=$1; prev_total=$2

while true; do
  sleep "$INTERVAL"

  set -- $(cpu_sample); idle=$1; total=$2
  cpu=$(awk -v a="$prev_idle" -v b="$prev_total" -v c="$idle" -v d="$total" 'BEGIN { dt=d-b; printf "%.1f", (dt > 0) ? (1 - (c-a)/dt) * 100 : 0 }')
  prev_idle=$idle; prev_total=$total

  set -- $(awk '/^MemTotal:/{t=$2} /^MemAvailable:/{a=$2} /^(MemFree|Buffers|Cached):/{f+=$2} END { if (a == 0) a = f; printf "%.1f %.2f", (t-a)/t*100, t/1048576 }' /proc/meminfo)
  mem=$1; mem_total=$2

  set -- $(df -Pk /var/lib/postgresql | awk 'NR==2 { printf "%.1f %.2f %.2f", $3/($3+$4)*100, $3/1048576, $2/1048576 }')
  disk=$1; disk_used=$2; disk_total=$3

  load=$(cut -d' ' -f1 /proc/loadavg)
  cores=$(grep -c '^processor' /proc/cpuinfo)
  uptime_h=$(awk '{ printf "%.1f", $1/3600 }' /proc/uptime)

  newest=$(ls -t /backups/*.dump 2>/dev/null | head -n 1)
  if [ -n "$newest" ]; then
    backup=$(awk -v now="$(date +%s)" -v m="$(stat -c %Y "$newest")" 'BEGIN { printf "%.1f", (now-m)/3600 }')
  else
    backup=null
  fi

  if pg_isready -q -h db -U casco -d casco; then pg=true; else pg=false; fi

  body=$(printf '{"cpu":%s,"memory":%s,"memoryTotalGb":%s,"disk":%s,"diskUsedGb":%s,"diskTotalGb":%s,"load1":%s,"cores":%s,"backupAgeHours":%s,"uptimeHours":%s,"pgReady":%s}' \
    "$cpu" "$mem" "$mem_total" "$disk" "$disk_used" "$disk_total" "$load" "$cores" "$backup" "$uptime_h" "$pg")

  wget -q -O /dev/null -T 15 \
    --header "Authorization: Bearer $MONITOR_TOKEN" \
    --header "Content-Type: application/json" \
    --post-data "$body" "$MONITOR_URL" || echo "monitor: could not reach $MONITOR_URL"
done
