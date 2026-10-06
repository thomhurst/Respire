#!/bin/sh
set -eu
set -f

# Bound each local command independently; Compose also bounds the entire check.
redis() { timeout 1 redis-cli -e --raw "$@"; }
for port in 27100 27101 27102; do
    quorum=$(redis -p "$port" SENTINEL CKQUORUM sample-primary)
    case "$quorum" in 'OK '*) ;; *) exit 1 ;; esac
done

master=$(redis -p 27100 SENTINEL GET-MASTER-ADDR-BY-NAME sample-primary)
set -- $master
[ "$#" -eq 2 ] && [ "$1" = 127.0.0.1 ] || exit 1
case "$2" in 7100) replica_port=7101 ;; 7101) replica_port=7100 ;; *) exit 1 ;; esac
master_port=$2
role=$(redis -p "$master_port" ROLE)
[ "$(printf '%s\n' "$role" | head -n 1)" = master ] || exit 1

replication=$(redis -p "$replica_port" INFO replication | tr -d '\r')
printf '%s\n' "$replication" | grep -Fxq 'role:slave'
printf '%s\n' "$replication" | grep -Fxq 'master_link_status:up'
printf '%s\n' "$replication" | grep -Fxq 'master_host:127.0.0.1'
printf '%s\n' "$replication" | grep -Fxq "master_port:$master_port"

# Validate one complete Sentinel replica record, rather than combining fields from different rows.
replicas=$(redis -p 27100 SENTINEL REPLICAS sample-primary)
printf '%s\n' "$replicas" | awk -v wanted_port="$replica_port" -v wanted_master="$master_port" '
    function ready() {
        return ip == "127.0.0.1" && port == wanted_port && flags == "slave" &&
            link == "ok" && priority > 0 && master_host == "127.0.0.1" && master_port == wanted_master
    }
    NR % 2 == 1 { key = $0; next }
    key == "name" {
        if (ready()) found = 1
        ip = port = flags = link = master_host = master_port = ""
        priority = 0
    }
    key == "ip" { ip = $0 }
    key == "port" { port = $0 }
    key == "flags" { flags = $0 }
    key == "master-link-status" { link = $0 }
    key == "slave-priority" { priority = $0 + 0 }
    key == "master-host" { master_host = $0 }
    key == "master-port" { master_port = $0 }
    END { exit !(found || ready()) }
'
