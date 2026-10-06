#!/bin/sh
set -eu
pids=''
cleanup() { kill $pids 2>/dev/null || true; }
trap cleanup EXIT
trap 'exit 0' INT TERM

for port in 7000 7001 7002; do
    mkdir -p "/data/$port"
    redis-server --port "$port" --dir "/data/$port" --bind 0.0.0.0 --protected-mode no \
        --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 1000 \
        --cluster-announce-ip 127.0.0.1 --cluster-announce-port "$port" \
        --save '' --appendonly no > "/data/$port/server.log" 2>&1 &
    pids="$pids $!"
done

for port in 7000 7001 7002; do
    attempt=0
    until redis-cli -p "$port" PING 2>/dev/null | grep -q PONG; do
        attempt=$((attempt + 1))
        [ "$attempt" -lt 30 ] || { cat "/data/$port/server.log"; exit 1; }
        sleep 1
    done
done
redis-cli -e -p 7000 CLUSTER SET-CONFIG-EPOCH 1
redis-cli -e -p 7001 CLUSTER SET-CONFIG-EPOCH 2
redis-cli -e -p 7002 CLUSTER SET-CONFIG-EPOCH 3
redis-cli -e -p 7000 CLUSTER ADDSLOTSRANGE 0 5460
redis-cli -e -p 7001 CLUSTER ADDSLOTSRANGE 5461 10921
redis-cli -e -p 7002 CLUSTER ADDSLOTSRANGE 10922 16383
redis-cli -e -p 7000 CLUSTER MEET 127.0.0.1 7001
redis-cli -e -p 7000 CLUSTER MEET 127.0.0.1 7002
wait
