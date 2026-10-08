#!/bin/sh
set -eu
primary=${1:-6380}
replica=${2:-6381}

for port in "$primary" "$replica"; do
  cat > "/tmp/respire-cluster-$port.conf" <<CONF
port $port
bind 0.0.0.0
protected-mode no
cluster-enabled yes
cluster-config-file /tmp/respire-cluster-$port-nodes.conf
cluster-announce-ip 127.0.0.1
cluster-announce-port $port
cluster-announce-bus-port $((port + 10000))
appendonly no
save ""
CONF
  redis-server "/tmp/respire-cluster-$port.conf" --daemonize yes
done
test "$(redis-cli -p "$primary" CLUSTER ADDSLOTSRANGE 0 16383)" = OK
primary_id=$(redis-cli -p "$primary" CLUSTER MYID)
test "$(redis-cli -p "$replica" CLUSTER MEET 127.0.0.1 "$primary")" = OK
attempt=0
until redis-cli -p "$replica" CLUSTER NODES | grep -q "$primary_id"; do
  attempt=$((attempt + 1))
  if [ "$attempt" -ge 45 ]; then echo 'Replica did not discover its primary.' >&2; exit 1; fi
  sleep 1
done
test "$(redis-cli -p "$replica" CLUSTER REPLICATE "$primary_id")" = OK
attempt=0
until redis-cli -p "$primary" CLUSTER INFO | tr -d '\r' | grep -qx 'cluster_state:ok' \
  && redis-cli -p "$replica" CLUSTER INFO | tr -d '\r' | grep -qx 'cluster_state:ok' \
  && redis-cli -p "$replica" INFO replication | tr -d '\r' | grep -qx 'master_link_status:up' \
  && redis-cli -p "$replica" INFO replication | tr -d '\r' | grep -qx 'master_sync_in_progress:0' \
  && redis-cli -p "$primary" --raw CLUSTER SLOTS | tr -d '\r' | grep -qx "$replica"; do
  attempt=$((attempt + 1))
  if [ "$attempt" -ge 45 ]; then echo 'Cluster replication did not become ready.' >&2; exit 1; fi
  sleep 1
done
redis-cli -p "$primary" CLUSTER NODES
redis-cli -p "$replica" INFO replication
