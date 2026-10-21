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


if __name__ == "__main__":
    unittest.main()
