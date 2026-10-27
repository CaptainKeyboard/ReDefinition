"""The machine a run happened on: its GPUs and drivers, CPU and memory as Windows names them, and the repository's commit."""
import json
import subprocess

from .constants import REPO


def machine_record():
    """The GPUs and drivers as Windows names them, the CPU and the memory."""
    record = {"adapters": [], "cpu": None, "memory_gb": None, "os": None}
    script = ("$v = Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, "
              "CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate; "
              "$c = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name; "
              "$m = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory; "
              "$o = (Get-CimInstance Win32_OperatingSystem); "
              "@{adapters=@($v); cpu=$c; memory=$m; os=($o.Caption + ' ' + $o.BuildNumber)} | ConvertTo-Json -Depth 4")
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True, timeout=60).stdout
        data = json.loads(out)
    except (OSError, ValueError, subprocess.TimeoutExpired):
        return record
    for a in data.get("adapters") or []:
        record["adapters"].append({
            "name": a.get("Name"), "driver": a.get("DriverVersion"),
            "nvidia_driver": nvidia_driver_from_windows(a.get("DriverVersion")) if "NVIDIA" in (a.get("Name") or "") else None,
            "resolution": "{}x{}".format(a.get("CurrentHorizontalResolution"), a.get("CurrentVerticalResolution"))
            if a.get("CurrentHorizontalResolution") else None,
            "refresh_hz": a.get("CurrentRefreshRate")})
    record["cpu"] = (data.get("cpu") or "").strip() or None
    if data.get("memory"):
        record["memory_gb"] = round(int(data["memory"]) / 2 ** 30, 1)
    record["os"] = data.get("os")
    return record


def nvidia_driver_from_windows(version):
    """NVIDIA's driver number from Windows' version of it: 32.0.15.9716 is 597.16."""
    if not version:
        return None
    digits = version.replace(".", "")[-5:]
    return "{}.{}".format(digits[:3], digits[3:]) if len(digits) == 5 and digits.isdigit() else None


def vendor_of(machine):
    """The vendor of the machine record's first adapter that names one ("NVIDIA", "AMD", "Intel"), or None."""
    for a in machine.get("adapters", []):
        name = (a.get("name") or "").upper()
        for vendor in ("NVIDIA", "AMD", "INTEL"):
            if vendor in name or (vendor == "AMD" and "RADEON" in name):
                return vendor if vendor != "INTEL" else "Intel"
    return None


def git_record():
    """The repository's commit, short commit, branch, and whether tracked files have local changes."""
    def git(*args):
        try:
            return subprocess.run(["git"] + list(args), cwd=REPO, capture_output=True, text=True).stdout.strip()
        except OSError:
            return ""
    return {"commit": git("rev-parse", "HEAD"), "short": git("rev-parse", "--short", "HEAD"),
            "branch": git("rev-parse", "--abbrev-ref", "HEAD"), "dirty": bool(git("status", "--porcelain", "--untracked-files=no"))}
