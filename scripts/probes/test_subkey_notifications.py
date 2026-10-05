"""Docker endpoint regressions; run with python -m unittest discover -s scripts/probes."""

import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("subkey_probe", Path(__file__).with_name("subkey-notifications.py"))
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class LocalDaemonTests(unittest.TestCase):
    def test_local_context_uses_selected_endpoint(self):
        for endpoint in ("unix:///var/run/docker.sock", "npipe:////./pipe/dockerDesktopLinuxEngine"):
            with self.subTest(endpoint=endpoint), patch.dict(probe.os.environ, {}, clear=True), \
                    patch.object(probe, "docker", side_effect=["desktop-linux", endpoint]) as docker:
                probe.verify_local_daemon()
                self.assertEqual(docker.call_args_list[1].args,
                                 ("context", "inspect", "desktop-linux", "--format", "{{.Endpoints.docker.Host}}"))

    def test_host_override_skips_context(self):
        with patch.dict(probe.os.environ, {"DOCKER_HOST": "unix:///custom/docker.sock"}, clear=True), \
                patch.object(probe, "docker") as docker:
            probe.verify_local_daemon()
            docker.assert_not_called()

    def test_context_override_wins_over_host(self):
        with patch.dict(probe.os.environ, {"DOCKER_CONTEXT": "local", "DOCKER_HOST": "ssh://remote"}, clear=True), \
                patch.object(probe, "docker", return_value="unix:///var/run/docker.sock") as docker:
            probe.verify_local_daemon()
            docker.assert_called_once_with("context", "inspect", "local", "--format", "{{.Endpoints.docker.Host}}")

    def test_remote_context_fails_before_container_or_socket(self):
        with patch.dict(probe.os.environ, {"DOCKER_CONTEXT": "remote"}, clear=True), \
                patch.object(probe, "docker", return_value="ssh://example.com") as docker, \
                patch.object(probe.socket, "create_connection") as connect:
            with self.assertRaisesRegex(RuntimeError, "requires a local Docker"):
                probe.main()
            docker.assert_called_once_with("context", "inspect", "remote", "--format", "{{.Endpoints.docker.Host}}")
            connect.assert_not_called()

    def test_network_endpoints_fail_before_docker_operations(self):
        for endpoint in ("ssh://example.com", "tcp://example.com:2376", "tcp://127.0.0.1:2375",
                         "npipe:////remote/pipe/docker_engine", "unix://remote/docker.sock"):
            with self.subTest(endpoint=endpoint), patch.dict(probe.os.environ, {"DOCKER_HOST": endpoint}, clear=True), \
                    patch.object(probe, "docker") as docker:
                with self.assertRaisesRegex(RuntimeError, "requires a local Docker"):
                    probe.main()
                docker.assert_not_called()


if __name__ == "__main__":
    unittest.main()
