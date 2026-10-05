"""Reproduce the subkey-cache spike against an owned, disposable Redis container.

Requires Python 3.10+ and Docker. No Python packages are required.
Run from the repository root: python scripts/probes/subkey-notifications.py
"""

import json
import socket
import subprocess
import time


def docker(*arguments):
    return subprocess.check_output(["docker", *arguments], text=True).strip()


class Connection:
    def __init__(self, port):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=5)
        self.stream = self.socket.makefile("rb")

    def close(self):
        self.stream.close()
        self.socket.close()

    def send(self, *arguments):
        arguments = [str(argument).encode() for argument in arguments]
        self.socket.sendall(
            b"*%d\r\n" % len(arguments)
            + b"".join(b"$%d\r\n" % len(value) + value + b"\r\n" for value in arguments)
        )

    def read(self):
        line = self.stream.readline()
        if not line:
            raise EOFError("Redis closed the connection")
        kind, value = line[:1], line[1:-2]
        if kind == b"-":
            raise RuntimeError(value.decode())
        if kind == b"+":
            return value.decode()
        if kind == b":":
            return int(value)
        if kind == b"_":
            return None
        if kind == b"$":
            count = int(value)
            if count == -1:
                return None
            payload = self.stream.read(count)
            assert len(payload) == count and self.stream.read(2) == b"\r\n"
            return payload.decode()
        if kind in (b"*", b">", b"~"):
            count = int(value)
            return None if count == -1 else [self.read() for _ in range(count)]
        if kind == b"%":
            return {self.read(): self.read() for _ in range(int(value))}
        raise ValueError(f"Unexpected RESP frame: {line!r}")

    def command(self, *arguments):
        self.send(*arguments)
        return self.read()

    def subscribe(self):
        assert self.command("PSUBSCRIBE", "__*") == ["psubscribe", "__*", 1]

    def events(self):
        self.send("PING", "barrier")
        messages = []
        while True:
            message = self.read()
            if message == ["pong", "barrier"]:
                return messages
            assert message[0] == "pmessage", message
            messages.append(message)


def check_events(messages, event, subkey):
    assert any(message[2].startswith("__keyspace@") and message[3] == event for message in messages), messages
    subkeys = [message for message in messages if message[2].startswith("__subkey")]
    if subkey:
        assert any(message[3] == "hset|5:field" for message in subkeys), messages
    else:
        assert not subkeys, messages


def report(name, evidence):
    print(f"PASS {name}: {json.dumps(evidence)}", flush=True)


def run(port):
    connections = []

    def connect():
        connection = Connection(port)
        connections.append(connection)
        return connection

    try:
        writer = connect()
        version = writer.command("INFO", "server")
        version = next(line for line in version.splitlines() if line.startswith("redis_version:"))
        print(version, flush=True)
        assert version == "redis_version:8.8.3", version
        assert writer.command("CONFIG", "SET", "notify-keyspace-events", "KSA") == "OK"
        subscriber = connect()
        subscriber.subscribe()

        assert writer.command("HSET", "control", "field", "value") == 1
        messages = subscriber.events()
        check_events(messages, "hset", subkey=True)
        report("hash field positive control", messages)

        assert writer.command("DEL", "control") == 1
        messages = subscriber.events()
        check_events(messages, "del", subkey=False)
        report("whole-key deletion", messages)

        json_evidence = []
        for path, value in (("$", '{"field":1}'), ("$.field", "2")):
            assert writer.command("JSON.SET", "document", path, value) == "OK"
            messages = subscriber.events()
            check_events(messages, "json.set", subkey=False)
            json_evidence.append(messages)
        report("JSON root and path writes", json_evidence)

        writer.command("HSET", "hash", "field", "before")
        subscriber.events()
        tracking = connect()
        assert tracking.command("HELLO", 3)["proto"] == 3
        assert tracking.command("CLIENT", "TRACKING", "ON") == "OK"
        assert tracking.command("HGET", "hash", "field") == "before"
        assert writer.command("DEL", "hash") == 1
        invalidation = tracking.read()
        assert invalidation == ["invalidate", ["hash"]], invalidation
        messages = subscriber.events()
        check_events(messages, "del", subkey=False)
        report("tracking invalidates deletion", {"tracking": invalidation, "events": messages})

        writer.command("HSET", "hash", "field", "before")
        subscriber.events()
        held = writer.command("HGET", "hash", "field")
        writer.command("HSET", "hash", "field", "after")
        messages = subscriber.events()
        check_events(messages, "hset", subkey=True)
        # Model delivery to the cache after the subscriber has consumed the invalidation.
        cache = {}
        cache.pop(("hash", "field"), None)
        cache[("hash", "field")] = held
        fresh = writer.command("HGET", "hash", "field")
        assert cache[("hash", "field")] == "before" and fresh == "after"
        report("delayed fill", {"held": held, "fresh": fresh, "events": messages})

        subscriber.close()
        connections.remove(subscriber)
        writer.command("HSET", "hash", "field", "while-disconnected")
        subscriber = connect()
        subscriber.subscribe()
        messages = subscriber.events()
        assert messages == [], messages
        report("no reconnect replay", messages)

        assert writer.command("CONFIG", "SET", "notify-keyspace-events", "") == "OK"
        writer.command("HSET", "hash", "field", "notifications-disabled")
        messages = subscriber.events()
        assert messages == [], messages
        report("silent notification disabling; PING still succeeds", messages)
    finally:
        for connection in connections:
            connection.close()


def main():
    container = docker("run", "--detach", "--rm", "--publish", "127.0.0.1::6379", "redis:8.8.3-alpine")
    try:
        print("image:", docker("inspect", "--format", "{{.Image}}", container), flush=True)
        port = int(docker("port", container, "6379/tcp").rsplit(":", 1)[1])
        deadline = time.monotonic() + 10
        while True:
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=1):
                    break
            except OSError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(0.1)
        run(port)
    finally:
        docker("stop", container)


if __name__ == "__main__":
    main()
