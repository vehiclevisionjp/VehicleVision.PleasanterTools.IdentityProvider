"""起動時の診断と再試行条件を Docker を置き換えて検証する。"""

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "wait_sqlserver", Path(__file__).resolve().parents[1] / "wait_sqlserver.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

RUNNING = {"Status": "running", "ExitCode": 0, "OOMKilled": False}
EXITED = {"Status": "exited", "ExitCode": 1, "OOMKilled": False}
KNOWN = "Reason: 0x00000002\nLast errno: 11\nResource temporarily unavailable"


class Docker:
    def __init__(self, states, logs=KNOWN, query_ok=True, start_ok=True, logs_ok=True):
        self.states = list(states)
        self.logs = logs
        self.query_ok = query_ok
        self.start_ok = start_ok
        self.logs_ok = logs_ok
        self.calls = []

    def __call__(self, *args, **kwargs):
        self.calls.append(args)
        code, output = 0, ""
        if args[0] == "inspect" and args[2] == "{{json .State}}":
            output = json.dumps(self.states[0])
            if len(self.states) > 1:
                self.states.pop(0)
        elif args[0] == "logs":
            code, output = (0 if self.logs_ok else 1), self.logs
        elif args[0] == "exec":
            code, output = (0 if self.query_ok else 1), "probe result"
        elif args[0] == "start":
            code = 0 if self.start_ok else 1
        return subprocess.CompletedProcess(args, code, output, "")

    @property
    def starts(self):
        return [call for call in self.calls if call[0] == "start"]


class StartupTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.target = module.Startup("test-sqlserver", self.directory.name)
        self.now = 0

    def sleep(self, seconds):
        self.now += seconds

    def run_wait(self, docker):
        with patch.object(module, "docker", docker), \
             patch.object(module.time, "monotonic", lambda: self.now), \
             patch.object(module.time, "sleep", self.sleep), \
             contextlib.redirect_stdout(io.StringIO()):
            self.target.wait(timeout=15)

    def test_ready_does_not_restart(self):
        docker = Docker([RUNNING])
        self.run_wait(docker)
        self.assertEqual([], docker.starts)
        query = next(call for call in docker.calls if call[0] == "exec")
        self.assertIn("SELECT 1", query)

    def test_known_exit_is_diagnosed_before_single_restart(self):
        docker = Docker([EXITED, RUNNING])
        self.run_wait(docker)
        self.assertEqual(1, len(docker.starts))
        names = [call[0] for call in docker.calls]
        self.assertLess(names.index("logs"), names.index("start"))
        self.assertIn(KNOWN, (Path(self.directory.name) / "exit-logs.txt").read_text())
        self.assertEqual(EXITED, json.loads(
            (Path(self.directory.name) / "exit-state.json").read_text()))

    def test_second_crash_fails(self):
        docker = Docker([EXITED, EXITED])
        with self.assertRaisesRegex(RuntimeError, "no further"):
            self.run_wait(docker)
        self.assertEqual(1, len(docker.starts))
        self.assertTrue((Path(self.directory.name) / "exit-after-retry-logs.txt").exists())

    def test_other_failure_and_oom_never_restart(self):
        cases = [
            (EXITED, "Password validation failed"),
            (EXITED, "Resource temporarily unavailable"),
            (dict(EXITED, OOMKilled=True), KNOWN),
            (dict(EXITED, ExitCode=137), KNOWN),
            (dict(EXITED, ExitCode=0), KNOWN),
        ]
        for state, logs in cases:
            with self.subTest(state=state, logs=logs):
                docker = Docker([state], logs=logs)
                with self.assertRaises(RuntimeError):
                    self.run_wait(docker)
                self.assertEqual([], docker.starts)

    def test_running_but_unresponsive_times_out_without_restart(self):
        docker = Docker([RUNNING], query_ok=False)
        with self.assertRaisesRegex(RuntimeError, "deadline"):
            self.run_wait(docker)
        self.assertEqual([], docker.starts)
        self.assertEqual(15, self.now)
        self.assertTrue((Path(self.directory.name) / "last-probe.txt").exists())
        self.assertTrue((Path(self.directory.name) / "timeout-state.json").exists())

    def test_restart_does_not_reset_deadline(self):
        docker = Docker([RUNNING, EXITED, RUNNING], query_ok=False)
        with self.assertRaisesRegex(RuntimeError, "deadline"):
            self.run_wait(docker)
        self.assertEqual(15, self.now)
        self.assertEqual(1, len(docker.starts))

    def test_restart_command_failure_is_not_ignored(self):
        docker = Docker([EXITED], start_ok=False)
        with self.assertRaisesRegex(RuntimeError, "restart failed"):
            self.run_wait(docker)
        self.assertEqual(1, len(docker.starts))

    def test_failed_log_read_is_not_evidence_for_restart(self):
        docker = Docker([EXITED], logs_ok=False)
        with self.assertRaises(RuntimeError):
            self.run_wait(docker)
        self.assertEqual([], docker.starts)

    def test_paused_container_is_not_restarted(self):
        docker = Docker([dict(RUNNING, Status="paused")])
        with self.assertRaises(RuntimeError):
            self.run_wait(docker)
        self.assertEqual([], docker.starts)

    def test_diagnostics_redact_password(self):
        docker = Docker([EXITED], logs="Password: test-secret")
        with patch.dict(os.environ, TESTENV_SA_PASSWORD="test-secret"):
            with self.assertRaises(RuntimeError):
                self.run_wait(docker)
        for file in Path(self.directory.name).iterdir():
            self.assertNotIn("test-secret", file.read_text())

    def test_password_uses_environment_and_command_is_bounded(self):
        with patch.dict(os.environ, TESTENV_SA_PASSWORD="test-secret"), \
             patch.object(module.subprocess, "run") as run:
            module.docker("exec", "-e", "SQLCMDPASSWORD", "test-sqlserver")
        args, kwargs = run.call_args
        self.assertNotIn("test-secret", " ".join(args[0]))
        self.assertEqual("test-secret", kwargs["env"]["SQLCMDPASSWORD"])
        self.assertEqual(15, kwargs["timeout"])


if __name__ == "__main__":
    unittest.main()
