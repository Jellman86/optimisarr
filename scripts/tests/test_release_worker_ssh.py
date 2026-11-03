import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from release_worker_ssh import ssh_request, relay, run_ssh, REMOTE_SUPERVISOR, WINDOWS_GUARDIAN


class SshLauncherTests(unittest.TestCase):
    def spec(self):
        return {"ssh": {"host": "test-alias", "python": "python3", "command": ["/packaged/AcceptanceWorker"],
                        "scratchRoot": "/private/scratch", "serverUrl": "http://test-server:{port}",
                        "tools": {"OPTIMISARR_FFMPEG": "/tools/ffmpeg", "OPTIMISARR_FFPROBE": "/tools/ffprobe"}}}

    def test_pin_travels_only_over_stdin_and_remote_paths_are_new(self):
        env = {"OPTIMISARR_ACCEPTANCE_PIN": "secret-pin", "OPTIMISARR_ACCEPTANCE_SERVER": "http://127.0.0.1:12345",
               "OPTIMISARR_ACCEPTANCE_NAME": "named-worker", "OPTIMISARR_ACCEPTANCE_ENCODER": "libx265",
               "OPTIMISARR_ADMIN_TOKEN": "must-not-forward", "OPTIMISARR_ACCEPTANCE_SCRATCH": "/local/scratch"}
        argv, payload = ssh_request(self.spec(), env, discover=False)
        self.assertNotIn("secret-pin", " ".join(argv))
        self.assertNotIn("must-not-forward", json.dumps(payload))
        self.assertEqual("secret-pin", payload["env"]["OPTIMISARR_ACCEPTANCE_PIN"])
        self.assertEqual("http://test-server:12345", payload["env"]["OPTIMISARR_ACCEPTANCE_SERVER"])
        self.assertTrue(payload["env"]["OPTIMISARR_ACCEPTANCE_SCRATCH"].startswith("/private/scratch/release-"))
        second = ssh_request(self.spec(), env, discover=False)[1]
        self.assertNotEqual(payload["env"]["OPTIMISARR_ACCEPTANCE_SCRATCH"], second["env"]["OPTIMISARR_ACCEPTANCE_SCRATCH"])
        self.assertIn("BatchMode=yes", argv)
        discovery = ssh_request(self.spec(), env, discover=True)[1]
        self.assertIn("--discover", discovery["command"])
        self.assertNotIn("OPTIMISARR_ACCEPTANCE_PIN", discovery["env"])

    def test_compressed_supervisor_fits_windows_command_line_and_preserves_its_source(self):
        import ast, base64, zlib
        argv, _ = ssh_request(self.spec(), {"OPTIMISARR_ACCEPTANCE_SERVER": "http://127.0.0.1:12345"}, discover=True)
        remote = argv[-1]
        self.assertLess(len(remote.encode("utf-16le")) // 2, 8000)
        program = ast.parse(remote.split(' -u -c "', 1)[1][:-1])
        encoded = next(node.value for node in ast.walk(program) if isinstance(node, ast.Constant) and isinstance(node.value, str))
        self.assertEqual(REMOTE_SUPERVISOR, zlib.decompress(base64.b64decode(encoded)).decode())

    def test_shell_metacharacters_in_config_cannot_become_ssh_options_or_code(self):
        env = {"OPTIMISARR_ACCEPTANCE_SERVER": "http://127.0.0.1:12345"}
        for changes in ({"host": "-oProxyCommand=bad"}, {"python": "python;bad"}, {"serverUrl": "http://production:8787"},
                        {"tools": {"OPTIMISARR_ADMIN_TOKEN": "bad"}}):
            spec = self.spec()
            spec["ssh"].update(changes)
            with self.assertRaises(AssertionError):
                ssh_request(spec, env, discover=True)

    def test_log_relay_removes_pin_and_shutdown_closes_only_owned_transport(self):
        import io
        output = io.StringIO()
        relay(io.StringIO("paired secret-pin\n"), output, ["secret-pin"])
        self.assertEqual("paired [redacted]\n", output.getvalue())
        process = Mock()
        process.poll.side_effect = [None, 0, 0]
        process.stdout, process.stderr = io.StringIO(""), io.StringIO("")
        process.wait.return_value = 0
        with patch("release_worker_ssh.subprocess.Popen", return_value=process):
            self.assertEqual(0, run_ssh(["ssh", "test-alias"], {"env": {}, "command": ["worker"]}, interval=0))
        process.stdin.close.assert_called_once()

    def test_remote_supervisor_returns_a_quick_childs_exit_code_while_its_stdin_stays_open(self):
        # A real supervisor process: discovery exits at once while the control stream is still open.
        # Interpreter shutdown must not abort on the blocked stdin reader and replace the child's code.
        import subprocess
        for code in (0, 3):
            supervisor = subprocess.Popen([sys.executable, "-u", "-c", REMOTE_SUPERVISOR], stdin=subprocess.PIPE,
                                          stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            request = {"command": [sys.executable, "-c", f"print('discovered'); raise SystemExit({code})"], "env": {}}
            supervisor.stdin.write((json.dumps(request) + "\n").encode())
            supervisor.stdin.flush()
            supervisor.wait(timeout=30)  # stdin deliberately left open, as over SSH
            stdout, stderr = supervisor.stdout.read(), supervisor.stderr.read()
            supervisor.stdin.close()
            supervisor.stdout.close()
            supervisor.stderr.close()
            self.assertEqual(code, supervisor.returncode, stderr.decode())
            self.assertIn(b"discovered", stdout)
            self.assertNotIn(b"Fatal Python error", stderr)

    def guardian(self, command):
        import subprocess
        return subprocess.Popen([sys.executable, "-u", "-c", WINDOWS_GUARDIAN, *command],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=0x01000000 if os.name == "nt" else 0)

    def observe(self, pid):
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes as w
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
            kernel.OpenProcess.restype = w.HANDLE
            kernel.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
            kernel.WaitForSingleObject.restype = w.DWORD
            kernel.TerminateProcess.argtypes = [w.HANDLE, w.UINT]
            kernel.CloseHandle.argtypes = [w.HANDLE]
            handle = kernel.OpenProcess(0x00100001, False, pid)
            self.assertTrue(handle, "Could not retain the fixture process handle")
            self.addCleanup(kernel.CloseHandle, handle)
            self.addCleanup(lambda: kernel.TerminateProcess(handle, 1) if kernel.WaitForSingleObject(handle, 0) == 258 else None)
            return lambda: kernel.WaitForSingleObject(handle, 0) == 0
        def exited():
            try:
                if sys.platform == "linux":
                    return Path(f"/proc/{pid}/stat").read_text().split(") ", 1)[1].startswith("Z")
                os.kill(pid, 0)  # POSIX only; this never runs on Windows.
                return False
            except (FileNotFoundError, ProcessLookupError):
                return True
        return exited

    def check_guardian_tree(self, ending):
        import tempfile, time
        with tempfile.TemporaryDirectory() as directory:
            marker, proceed = Path(directory) / "grandchild.pid", Path(directory) / "proceed"
            wait_for_observer = f"while not pathlib.Path({str(proceed)!r}).exists(): time.sleep(.02)"
            worker = ("import os, pathlib, subprocess, sys, time; "
                      "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)']); "
                      f"pathlib.Path({str(marker)!r}).write_text(str(os.getpid()) + ',' + str(child.pid)); "
                      f"exec({wait_for_observer!r}); "
                      "raise SystemExit(4)")
            guardian = self.guardian([sys.executable, "-c", worker])
            try:
                deadline = time.monotonic() + 15
                identities = None
                while identities is None and time.monotonic() < deadline:
                    try:
                        parsed = [int(value) for value in marker.read_text().split(',')]
                        if len(parsed) == 2: identities = parsed
                    except (FileNotFoundError, ValueError): time.sleep(.02)
                self.assertIsNotNone(identities, "The fixture did not start: " + str(guardian.poll()))
                # Retain both processes, so even a deliberately broken guardian used
                # for the red run cannot leak its worker after an assertion failure.
                root_exited = self.observe(identities[0])
                exited = self.observe(identities[1])
                if ending == "eof": guardian.stdin.close()
                elif ending == "root-exit": proceed.write_text("ready")
                else: guardian.kill()
                guardian.wait(timeout=15)
                deadline = time.monotonic() + 10
                while (not exited() or not root_exited()) and time.monotonic() < deadline: time.sleep(.02)
                self.assertTrue(exited(), "The worker's child outlived its guardian")
                self.assertTrue(root_exited(), "The fixture worker outlived its guardian")
                if ending == "root-exit": self.assertEqual(4, guardian.returncode)
            finally:
                if guardian.poll() is None: guardian.kill()
                guardian.wait(timeout=15)
                for stream in (guardian.stdin, guardian.stdout, guardian.stderr):
                    if not stream.closed: stream.close()

    def test_guardian_stops_its_worker_tree_when_the_supervisor_pipe_closes(self):
        self.check_guardian_tree("eof")

    def test_a_root_that_exits_cannot_leave_a_tool_running(self):
        self.check_guardian_tree("root-exit")

    @unittest.skipUnless(os.name == "nt", "Windows kernel debug lifetime")
    def test_a_crashed_windows_guardian_cannot_leave_its_worker_tree_running(self):
        self.check_guardian_tree("crash")

    def test_guardian_returns_its_workers_exit_code(self):
        guardian = self.guardian([sys.executable, "-c", "raise SystemExit(4)"])
        try:
            guardian.wait(timeout=15)
            self.assertEqual(4, guardian.returncode, guardian.stderr.read().decode())
        finally:
            for stream in (guardian.stdin, guardian.stdout, guardian.stderr): stream.close()

    def test_windows_supervisor_breaks_its_guardian_away_from_the_ssh_job(self):
        self.assertIn("0x01000000", REMOTE_SUPERVISOR)
        self.assertIn(repr(WINDOWS_GUARDIAN), REMOTE_SUPERVISOR)
        self.assertIn("DebugSetProcessKillOnExit", WINDOWS_GUARDIAN)
        self.assertNotIn('process = None\nif process is None', REMOTE_SUPERVISOR)

    def test_a_remote_that_already_finished_is_not_reported_as_an_unavailable_launcher(self):
        import io
        process = Mock()
        process.poll.side_effect = [None, 0, 0]
        process.stdout, process.stderr = io.StringIO(""), io.StringIO("")
        process.stdin.write.side_effect = [None, BrokenPipeError()]
        process.stdin.close.side_effect = BrokenPipeError()
        process.wait.return_value = 0
        with patch("release_worker_ssh.subprocess.Popen", return_value=process):
            self.assertEqual(0, run_ssh(["ssh", "test-alias"], {"env": {}, "command": ["worker"]}, interval=0))

    def test_a_launcher_whose_harness_died_stops_its_remote_worker(self):
        # A harness killed outright never runs its cleanup; its launchers must not outlive it.
        import io
        process = Mock()
        process.poll.return_value = None
        process.stdout, process.stderr = io.StringIO(""), io.StringIO("")
        process.wait.return_value = 0
        parents = iter([4242, 4242, 1])
        with patch("release_worker_ssh.subprocess.Popen", return_value=process), \
             patch("release_worker_ssh.os.getppid", side_effect=lambda: next(parents, 1)):
            self.assertEqual(0, run_ssh(["ssh", "test-alias"], {"env": {}, "command": ["worker"]}, interval=0))
        process.stdin.close.assert_called()


if __name__ == "__main__":
    unittest.main()
