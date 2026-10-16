"""CI の SQL Server 起動を確認し、既知の内部エラーだけ一度再試行する。"""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import time


def docker(*args, timeout=15):
    # パスワードはコマンドラインや診断に載せない。
    env = dict(os.environ, SQLCMDPASSWORD=os.environ.get("TESTENV_SA_PASSWORD", ""))
    try:
        return subprocess.run(
            ["docker", *args], capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=timeout, env=env,
        )
    except subprocess.TimeoutExpired:
        return subprocess.CompletedProcess(args, 124, "", "Docker command timed out")
    except OSError as error:
        return subprocess.CompletedProcess(args, 127, "", str(error))


class Startup:
    def __init__(self, container, output):
        self.container = container
        self.output = Path(output)

    def state(self):
        result = docker("inspect", "--format", "{{json .State}}", self.container)
        if result.returncode:
            raise RuntimeError("Cannot inspect SQL Server container: " + result.stderr)
        return json.loads(result.stdout)

    def diagnose(self, label, state=None):
        self.output.mkdir(parents=True, exist_ok=True)
        if state is None:
            try:
                state = self.state()
            except (RuntimeError, ValueError) as error:
                state = {"InspectionError": str(error)}
        # Config.Env は資格情報を含むため、inspect 全体は保存しない。
        self.save(label + "-state.json", json.dumps(state, indent=2))
        details = docker("inspect", "--format",
                         "Image={{.Image}} RestartCount={{.RestartCount}} "
                         "Memory={{.HostConfig.Memory}} "
                         "NanoCpus={{.HostConfig.NanoCpus}} "
                         "PidsLimit={{.HostConfig.PidsLimit}}", self.container)
        self.save(label + "-limits.txt", details.stdout + details.stderr)
        logs = docker("logs", "--timestamps", "--tail", "500", self.container)
        log_text = logs.stdout + logs.stderr
        self.save(label + "-logs.txt", log_text)
        resources = {"cpu_count": os.cpu_count(),
                     "disk_free_bytes": shutil.disk_usage(self.output).free}
        meminfo = Path("/proc/meminfo")
        if meminfo.exists():
            resources["meminfo"] = meminfo.read_text()
        self.save(label + "-host.json", json.dumps(resources, indent=2))
        print("SQL Server diagnostics: " + str(self.output / (label + "-state.json")))
        print((self.output / (label + "-state.json")).read_text(encoding="utf-8"))
        # 失敗した docker logs の出力を再起動の根拠にしない。
        return log_text if logs.returncode == 0 else ""

    def save(self, name, text):
        # 検証用資格情報でも、診断へ偶然含まれた場合は伏せる。
        password = os.environ.get("TESTENV_SA_PASSWORD")
        if password:
            text = text.replace(password, "[REDACTED]")
        (self.output / name).write_text(text, encoding="utf-8")

    def wait(self, timeout=300):
        deadline = time.monotonic() + timeout
        retried = False
        while time.monotonic() < deadline:
            state = self.state()
            status = state.get("Status")
            if status == "exited":
                logs = self.diagnose("exit-after-retry" if retried else "exit", state)
                # #461 で実測した組合せだけ。OOM や認証・設定エラーは隠さない。
                known_failure = (
                    state.get("ExitCode") == 1
                    and state.get("OOMKilled") is False
                    and "Reason: 0x00000002" in logs
                    and "Resource temporarily unavailable" in logs
                )
                if retried or not known_failure:
                    raise RuntimeError("SQL Server exited; no further automatic restart")
                retried = True
                print("::warning::SQL Server hit the known startup error; starting it once")
                result = docker("start", self.container)
                if result.returncode:
                    raise RuntimeError("SQL Server restart failed: " + result.stderr)
            elif status != "running":
                self.diagnose("unexpected-state", state)
                raise RuntimeError("SQL Server is not running: " + str(status))
            else:
                result = docker(
                    "exec", "-e", "SQLCMDPASSWORD", self.container,
                    "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa",
                    "-C", "-b", "-l", "2", "-t", "2", "-Q", "SELECT 1",
                    timeout=10,
                )
                if result.returncode == 0:
                    print("SQL Server is ready" + (" after one restart" if retried else ""))
                    return
                self.output.mkdir(parents=True, exist_ok=True)
                self.save("last-probe.txt", result.stdout + result.stderr)
            time.sleep(min(5, max(0, deadline - time.monotonic())))
        self.diagnose("timeout")
        raise RuntimeError("SQL Server did not become ready within the startup deadline")


def main():
    parser = argparse.ArgumentParser(description="Check SQL Server startup in CI")
    parser.add_argument("container")
    parser.add_argument("--output", default="TestResults/sqlserver-startup")
    parser.add_argument("--diagnose-only", action="store_true")
    args = parser.parse_args()
    if not args.container or args.container.startswith("-"):
        parser.error("A container ID or name is required")
    startup = Startup(args.container, args.output)
    if args.diagnose_only:
        startup.diagnose("final")
        return 0
    if not os.environ.get("TESTENV_SA_PASSWORD"):
        parser.error("TESTENV_SA_PASSWORD is required")
    try:
        startup.wait()
        return 0
    except (RuntimeError, ValueError) as error:
        startup.diagnose("failure")
        print("::error::" + str(error).replace(os.environ["TESTENV_SA_PASSWORD"], "[REDACTED]"))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
