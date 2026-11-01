import json
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from release_worker_ssh import ssh_request, relay, run_ssh, REMOTE_SUPERVISOR


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
            self.assertEqual(code, supervisor.returncode, stderr.decode())
            self.assertIn(b"discovered", stdout)
            self.assertNotIn(b"Fatal Python error", stderr)

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


if __name__ == "__main__":
    unittest.main()
