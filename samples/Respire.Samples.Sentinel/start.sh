#!/bin/sh
set -eu
pids=''
cleanup() { kill $pids 2>/dev/null || true; }
trap cleanup EXIT
trap 'exit 0' INT TERM

mkdir -p /data/7100 /data/7101
redis-server --port 7100 --dir /data/7100 --bind 0.0.0.0 --protected-mode no \
    --save '' --appendonly no > /data/7100/server.log 2>&1 &
pids="$pids $!"
redis-server --port 7101 --dir /data/7101 --bind 0.0.0.0 --protected-mode no \
    --replicaof 127.0.0.1 7100 --replica-announce-ip 127.0.0.1 --replica-announce-port 7101 \
    --save '' --appendonly no > /data/7101/server.log 2>&1 &
pids="$pids $!"

attempt=0
until redis-cli -p 7101 INFO replication 2>/dev/null | grep -q 'master_link_status:up'; do
    attempt=$((attempt + 1))
    [ "$attempt" -lt 30 ] || { cat /data/7100/server.log /data/7101/server.log; exit 1; }
    sleep 1
done

for port in 27100 27101 27102; do
    mkdir -p "/data/$port"
    cat > "/data/$port/sentinel.conf" <<EOF
port $port
bind 0.0.0.0
protected-mode no
dir /data/$port
sentinel announce-ip 127.0.0.1
sentinel announce-port $port
sentinel monitor sample-primary 127.0.0.1 7100 2
sentinel down-after-milliseconds sample-primary 1000
sentinel failover-timeout sample-primary 10000
sentinel parallel-syncs sample-primary 1
EOF
    redis-server "/data/$port/sentinel.conf" --sentinel > "/data/$port/server.log" 2>&1 &
    pids="$pids $!"
done
wait
