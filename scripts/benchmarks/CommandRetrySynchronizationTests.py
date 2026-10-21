import unittest
from CommandRetrySynchronization import (BASELINE, CANDIDATE, HEAD, validate_summary,
                                          validate_syscalls, verify_pins)


class SynchronizationTests(unittest.TestCase):
    def test_rejects_wrong_source_or_parent(self):
        for baseline, candidate, parents in [(HEAD, CANDIDATE, [BASELINE, HEAD]),
                                             (BASELINE, HEAD, [BASELINE, HEAD]),
                                             (BASELINE, CANDIDATE, [HEAD, BASELINE])]:
            with self.assertRaisesRegex(ValueError, "Sources"):
                verify_pins(baseline, candidate, parents, b"fixture")

    def test_rejects_changed_fixture(self):
        with self.assertRaisesRegex(ValueError, "Fixture"):
            verify_pins(BASELINE, CANDIDATE, [BASELINE, HEAD], b"changed")

    def test_requires_complete_lossless_task_evidence(self):
        good = dict(EventsLost=0, operations=512, complete=True, taskEvents=1)
        validate_summary(good)
        for field, value in [("EventsLost", 1), ("operations", 511), ("complete", False), ("taskEvents", 0)]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                validate_summary(dict(good, **{field: value}))

    def test_requires_socket_traffic_inside_matching_range(self):
        log = "SYNC_RANGE_START unixMicroseconds=1000000 pid=123\nSYNC_RANGE_STOP unixMicroseconds=2000000 pid=123"
        calls = ("1.200000 sendmsg(1<TCP:[127.0.0.1:50000->127.0.0.1:6379]>, ...) = 8 <0.1>\n"
                 "1.300000 recvmsg(1<TCP:[127.0.0.1:50000->127.0.0.1:6379]>, ...) = 8 <0.1>")
        self.assertEqual(2, validate_syscalls(calls, log)["recordsInRange"])
        for bad in [calls.replace("1.200000", "0.200000"), calls.replace("recvmsg", "futex"), "",
                    calls.replace(" = 8", " = -1 EAGAIN"), calls.replace("TCP:", "UNIX-STREAM:"),
                    calls.replace(":6379]", ":6380]")]:
            with self.assertRaises(ValueError):
                validate_syscalls(bad, log)
        for bad in [log.replace("pid=123", "pid=456", 1), log + "\n" + log,
                    log.replace("2000000", "1000000")]:
            with self.assertRaises(ValueError):
                validate_syscalls(calls, bad)

    def test_accepts_ipv4_and_bracketed_ipv6_redis_peers(self):
        log = "SYNC_RANGE_START unixMicroseconds=1000000 pid=123\nSYNC_RANGE_STOP unixMicroseconds=2000000 pid=123"
        peers = (
            "TCP:[127.0.0.1:50000->127.0.0.1:6379]",
            "TCPv6:[[::ffff:127.0.0.1]:50000->[::ffff:127.0.0.1]:6379]",
            "TCPv6:[[::1]:50000->[::1]:6379]",
        )
        for peer in peers:
            calls = (f"1.200000 sendmsg(1<{peer}>, ...) = 8 <0.1>\n"
                     f"1.300000 recvmsg(1<{peer}>, ...) = 8 <0.1>")
            with self.subTest(peer=peer):
                self.assertEqual(2, validate_syscalls(calls, log)["recordsInRange"])

    def test_rejects_ipv6_wrong_destination_and_failed_or_ipc_traffic(self):
        log = "SYNC_RANGE_START unixMicroseconds=1000000 pid=123\nSYNC_RANGE_STOP unixMicroseconds=2000000 pid=123"
        peer = "TCPv6:[[::ffff:127.0.0.1]:6379->[::ffff:127.0.0.1]:6379]"
        calls = (f"1.200000 sendmsg(1<{peer}>, ...) = 8 <0.1>\n"
                 f"1.300000 recvmsg(1<{peer}>, ...) = 8 <0.1>")
        for bad in (calls.replace("]:6379]>", "]:6380]>"),
                    calls.replace(" = 8", " = -1 EAGAIN"),
                    calls.replace("TCPv6:", "UNIX-STREAM:"),
                    calls.replace("->", "")):
            with self.subTest(calls=bad), self.assertRaises(ValueError):
                validate_syscalls(bad, log)

    def test_payload_cannot_supply_a_successful_return_value(self):
        log = "SYNC_RANGE_START unixMicroseconds=1000000 pid=123\nSYNC_RANGE_STOP unixMicroseconds=2000000 pid=123"
        peer = "TCP:[127.0.0.1:50000->127.0.0.1:6379]"
        for result in ("0", "-1 EAGAIN (Resource temporarily unavailable)"):
            for failed in ("sendmsg", "recvmsg"):
                calls = "\n".join(
                    f'1.200000 {name}(1<{peer}>, {{iov_base="payload) = 8 <0.1>"}}, 0) = '
                    f'{result if name == failed else "8"} <0.000001>'
                    for name in ("sendmsg", "recvmsg"))
                with self.subTest(result=result, failed=failed), self.assertRaises(ValueError):
                    validate_syscalls(calls, log)

    def test_payload_cannot_supply_the_redis_peer(self):
        log = "SYNC_RANGE_START unixMicroseconds=1000000 pid=123\nSYNC_RANGE_STOP unixMicroseconds=2000000 pid=123"
        fake = "<TCP:[127.0.0.1:50000->127.0.0.1:6379]>"
        for peer in ("TCP:[127.0.0.1:50000->127.0.0.1:6380]", "UNIX-STREAM:[123->456]"):
            calls = "\n".join(
                f'1.200000 {name}(1<{peer}>, {{iov_base="{fake}"}}, 0) = 8 <0.000001>'
                for name in ("sendmsg", "recvmsg"))
            with self.subTest(peer=peer), self.assertRaises(ValueError):
                validate_syscalls(calls, log)


if __name__ == "__main__":
    unittest.main()
