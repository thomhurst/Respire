#!/bin/sh
set -eu
primary=${1:-6380}
replica=${2:-6381}
redis-server --port "$primary" --bind 0.0.0.0 --protected-mode no --save '' --appendonly no --daemonize yes
redis-server --port "$replica" --bind 0.0.0.0 --protected-mode no --save '' --appendonly no \
  --replicaof 127.0.0.1 "$primary" --daemonize yes
attempt=0
until redis-cli -p "$replica" INFO replication | tr -d '\r' | grep -qx 'master_link_status:up' \
  && redis-cli -p "$replica" INFO replication | tr -d '\r' | grep -qx 'master_sync_in_progress:0'; do
  attempt=$((attempt + 1))
  if [ "$attempt" -ge 45 ]; then echo 'Standalone replication did not become ready.' >&2; exit 1; fi
  sleep 1
done
redis-cli -p "$primary" INFO replication
redis-cli -p "$replica" INFO replication
