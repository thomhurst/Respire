"""Run the real topology samples with bounded, owned Compose lifecycles."""

import argparse
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import time
import uuid


ROOT = Path(__file__).resolve().parent.parent


def validate_cluster(output):
    rows = re.findall(r"^PASS: respire:cluster-sample:\{([abc])\}, slot (\d+)\s*$", output, re.M)
    if len(rows) != 3 or {tag for tag, _ in rows} != {"a", "b", "c"}:
        raise RuntimeError("Cluster output must contain exactly one successful row per sample key.")
    slots = [int(slot) for _, slot in rows]
    if not all(0 <= slot <= 16383 for slot in slots):
        raise RuntimeError("Cluster output contains an invalid slot.")
    ranges = {0 if slot <= 5460 else 1 if slot <= 10921 else 2 for slot in slots}
    if ranges != {0, 1, 2} or "Discovered 3 shards from one seed." not in output:
        raise RuntimeError("Cluster sample did not reach all three configured slot ranges.")
    if "Cluster sample completed; all three values round-tripped." not in output:
        raise RuntimeError("Cluster sample completion is missing.")


def has_primary(output, port):
    return re.search(rf"^PASS \d+: primary 127\.0\.0\.1:{port}\s*$", output, re.M) is not None


def validate_sentinel(output, before, after):
    if before == after or {before, after} != {7100, 7101}:
        raise RuntimeError("Sentinel did not promote the other configured primary endpoint.")
    if not all(has_primary(output, port) for port in (before, after)):
        raise RuntimeError("The same sample process must report success from both primary endpoints.")
    if not re.search(r"^Sentinel sample completed: [1-9]\d* successful round trips\.\s*$", output, re.M):
        raise RuntimeError("Sentinel sample completion is missing.")


class Smoke:
    def __init__(self, args):
        self.args = args
        self.logs = args.artifacts.resolve()
        self.logs.mkdir(parents=True, exist_ok=True)
        self.compose = ["docker", "compose", "-p", args.project, "-f",
                        str(ROOT / "samples" / f"Respire.Samples.{args.sample}" / "compose.yaml")]
        self.service = args.sample.lower()
        self.sequence = 0
        self.owned = False
        self.sample = None
        self.sample_log = self.logs / "sample.log"

    def start(self, command, log, environment=None):
        stream = log.open("w", encoding="utf-8")
        try:
            process = subprocess.Popen(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT,
                                       env=environment, start_new_session=os.name != "nt",
                                       creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        except BaseException:
            stream.close()
            raise
        return process, stream

    @staticmethod
    def stop(handle):
        process, stream = handle
        try:
            if process.poll() is None:
                try:
                    if os.name == "nt":
                        # The PowerShell guard owns a kill-on-close Windows job for its .NET child.
                        process.kill()
                    else:
                        os.killpg(process.pid, signal.SIGINT)
                except ProcessLookupError:
                    pass
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    if os.name == "nt":
                        process.kill()
                    else:
                        os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)
        finally:
            stream.close()

    def run(self, command, name, timeout=15):
        self.sequence += 1
        log = self.logs / f"{self.sequence:03d}-{name}.log"
        handle = self.start(command, log)
        try:
            code = handle[0].wait(timeout=timeout)
            if code:
                raise RuntimeError(f"{name} exited {code}; see {log}")
        finally:
            self.stop(handle)
        return log.read_text(encoding="utf-8", errors="replace").strip()

    @staticmethod
    def dotnet(arguments, timeout):
        def quote(value):
            return "'" + str(value).replace("'", "''") + "'"
        guard = quote(ROOT / "scripts" / "Invoke-AgentDotNet.ps1")
        # Invoke the existing guard in-process in PowerShell on both platforms.
        command = f"& {guard} -SingleNode -TimeoutSeconds {timeout} -DotNetArguments @(" + ",".join(map(quote, arguments)) + ")"
        return ["pwsh", "-NoProfile", "-Command", command]

    def redis(self, *arguments):
        return self.run(self.compose + ["exec", "-T", self.service, "redis-cli", *arguments], "redis")

    def primary(self):
        address = json.loads(self.redis("--json", "-p", "27100", "SENTINEL", "GET-MASTER-ADDR-BY-NAME", "sample-primary"))
        if len(address) != 2 or address[0] != "127.0.0.1" or int(address[1]) not in (7100, 7101):
            raise RuntimeError(f"Unexpected Sentinel primary: {address}")
        return int(address[1])

    def wait_for(self, predicate, seconds, description):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate():
                return
            if self.sample and self.sample[0].poll() is not None:
                raise RuntimeError(f"Sample exited before {description}; see {self.sample_log}")
            time.sleep(0.2)
        raise TimeoutError(f"Timed out waiting for {description} after {seconds} seconds.")

    def output(self):
        return self.sample_log.read_text(encoding="utf-8", errors="replace")

    def execute(self):
        # Refuse a pre-existing project before assuming cleanup ownership.
        selector = f"label=com.docker.compose.project={self.args.project}"
        for resource in ("container", "network", "volume"):
            command = ["docker", resource, "ls"] + (["--all"] if resource == "container" else [])
            command += ["--filter", selector, "--format", "{{.Name}}" if resource == "volume" else "{{.ID}}"]
            if self.run(command, f"existing-{resource}"):
                raise RuntimeError("Compose project already exists; refusing to reuse or clean it.")
        self.owned = True
        (self.logs / "owned-project.txt").write_text(self.args.project, encoding="utf-8")
        project = f"samples/Respire.Samples.{self.args.sample}"
        self.run(self.dotnet(["build", project, "-c", "Release", "-f", self.args.framework], 600), "build", 630)
        self.run(self.compose + ["build"], "compose-build", 180)
        self.run(self.compose + ["up", "-d", "--wait", "--wait-timeout", "60"], "compose-up", 90)
        environment = os.environ.copy()
        environment["RESPIRE_CONNECTION"] = ("127.0.0.1:7000,cluster=true" if self.args.sample == "Cluster"
            else "127.0.0.1:27100,127.0.0.1:27101,127.0.0.1:27102,serviceName=sample-primary")
        dll = ROOT / project / "bin" / "Release" / self.args.framework / f"Respire.Samples.{self.args.sample}.dll"
        before = self.primary() if self.args.sample == "Sentinel" else None
        arguments = [str(dll)] + (["60"] if self.args.sample == "Sentinel" else [])
        self.sample = self.start(self.dotnet(arguments, 100), self.sample_log, environment)
        if before is not None:
            self.wait_for(lambda: has_primary(self.output(), before), 20, "initial primary success")
            if self.redis("--raw", "-p", "27100", "SENTINEL", "FAILOVER", "sample-primary") != "OK":
                raise RuntimeError("Sentinel rejected the promotion request.")
            self.wait_for(lambda: self.primary() != before, 30, "Sentinel promotion")
            after = self.primary()
            self.wait_for(lambda: has_primary(self.output(), after), 30, "same-client success on promoted primary")
            (self.logs / "promotion.json").write_text(json.dumps({"before": before, "after": after}), encoding="utf-8")
        code = self.sample[0].wait(timeout=110)
        if code:
            raise RuntimeError(f"Sample exited {code}; see {self.sample_log}")
        if before is None:
            validate_cluster(self.output())
            self.redis("--json", "-p", "7000", "CLUSTER", "SLOTS")
        else:
            validate_sentinel(self.output(), before, after)
        print(f"PASS: {self.args.sample} {self.args.framework} smoke", flush=True)

    def cleanup(self):
        try:
            if self.sample:
                self.stop(self.sample)
        finally:
            if self.owned:
                self.cleanup_compose()

    def cleanup_compose(self):
        for arguments, name in ((["logs", "--no-color"], "compose-logs"),
                                (["exec", "-T", self.service, "sh", "-c", "cat /data/*/server.log"], "server-logs")):
            try:
                self.run(self.compose + arguments, name)
            except Exception as error:
                print(f"Diagnostic capture: {error}", flush=True)
        self.run(self.compose + ["down", "--volumes", "--remove-orphans", "--timeout", "10"], "compose-down", 45)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sample", choices=("Cluster", "Sentinel"), required=True)
    parser.add_argument("--framework", choices=("net8.0", "net10.0"), required=True)
    parser.add_argument("--project", default="respire-smoke-" + uuid.uuid4().hex)
    parser.add_argument("--artifacts", type=Path, required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[a-z0-9][a-z0-9_-]+", args.project):
        parser.error("Use a lowercase Compose project name with letters, numbers, hyphens, or underscores.")
    def cancel(_signal, _frame):
        raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM, cancel)
    smoke = Smoke(args)
    try:
        smoke.execute()
    finally:
        smoke.cleanup()


if __name__ == "__main__":
    main()
