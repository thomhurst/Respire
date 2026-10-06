#!/bin/sh
set -eu

# All advertised endpoints use the same ports inside and outside this one container.
# This also works with Docker Desktop without host networking or host DNS aliases.
redis-server --port 19580 --bind 0.0.0.0 --protected-mode no --save '' --appendonly no --daemonize yes
redis-server --port 19581 --bind 0.0.0.0 --protected-mode no --save '' --appendonly no \
  --replicaof 127.0.0.1 19580 --daemonize yes
redis-server --port 19583 --bind 0.0.0.0 --protected-mode no --save '' --appendonly no \
  --cluster-enabled yes --cluster-config-file /tmp/benchmark-cluster.conf \
  --cluster-announce-ip 127.0.0.1 --cluster-announce-port 19583 --daemonize yes

# A single owner of every slot exercises Cluster routing without redirects or failover noise.
for port in 19580 19581 19583; do
  attempts=0
  until redis-cli -p "$port" PING 2>/dev/null | grep -q PONG; do
    attempts=$((attempts + 1))
    if [ "$attempts" -ge 60 ]; then exit 1; fi
    sleep 1
  done
done
redis-cli -p 19583 CLUSTER ADDSLOTSRANGE 0 16383
cat > /tmp/benchmark-sentinel.conf <<'EOF'
port 19582
bind 0.0.0.0
protected-mode no
sentinel monitor benchmark 127.0.0.1 19580 1
sentinel down-after-milliseconds benchmark 60000
EOF
exec redis-server /tmp/benchmark-sentinel.conf --sentinel
