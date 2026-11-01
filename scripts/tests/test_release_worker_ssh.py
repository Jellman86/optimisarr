import json
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from release_worker_ssh import ssh_request, relay, run_ssh


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


if __name__ == "__main__":
    unittest.main()
