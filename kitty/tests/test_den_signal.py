from __future__ import annotations

import fcntl
import os
import pathlib
import pty
import subprocess
import termios
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
SIGNAL_SCRIPT = REPO_ROOT / "kitty" / "den-signal.sh"


class DenSignalTests(unittest.TestCase):
    def run_helper(
        self,
        *,
        stdin_target: int,
        stdout_target,
        stderr_target,
        preexec_fn=None,
    ) -> subprocess.Popen[str]:
        env = os.environ.copy()
        env["KITTY_WINDOW_ID"] = "77"
        command = f"source {SIGNAL_SCRIPT}; den_signal den_status working"
        return subprocess.Popen(
            ["bash", "-lc", command],
            cwd=REPO_ROOT,
            env=env,
            stdin=stdin_target,
            stdout=stdout_target,
            stderr=stderr_target,
            text=True,
            preexec_fn=preexec_fn,
        )

    def test_interactive_tty_receives_user_var_signal(self) -> None:
        master_fd, slave_fd = pty.openpty()
        self.addCleanup(os.close, master_fd)
        self.addCleanup(os.close, slave_fd)

        proc = self.run_helper(
            stdin_target=slave_fd,
            stdout_target=slave_fd,
            stderr_target=slave_fd,
        )
        self.addCleanup(lambda: proc.kill() if proc.poll() is None else None)
        proc.wait(timeout=5)

        output = os.read(master_fd, 4096).decode("utf-8", errors="replace")

        self.assertEqual(0, proc.returncode)
        self.assertIn("SetUserVar=den_status=d29ya2luZw==", output)

    def test_piped_stdout_stays_clean_while_signal_goes_to_controlling_tty(self) -> None:
        master_fd, slave_fd = pty.openpty()
        self.addCleanup(os.close, master_fd)
        self.addCleanup(os.close, slave_fd)

        def attach_controlling_tty() -> None:
            os.setsid()
            fcntl.ioctl(slave_fd, termios.TIOCSCTTY, 0)

        proc = self.run_helper(
            stdin_target=slave_fd,
            stdout_target=subprocess.PIPE,
            stderr_target=subprocess.PIPE,
            preexec_fn=attach_controlling_tty,
        )
        self.addCleanup(lambda: proc.kill() if proc.poll() is None else None)

        stdout, stderr = proc.communicate(timeout=5)
        tty_output = os.read(master_fd, 4096).decode("utf-8", errors="replace")

        self.assertEqual(0, proc.returncode, stderr)
        self.assertEqual("", stdout)
        self.assertIn("SetUserVar=den_status=d29ya2luZw==", tty_output)


if __name__ == "__main__":
    unittest.main()
