"""Summarise the FEAT-PERF-28 lines of one or more godot.log files, one block per log.

    python -I arrival_summary.py <godot.log> [<godot.log> ...]

Reads only; changes nothing.
"""
import re
import sys

NUM = r"([0-9.]+)"


def field(line, name):
    m = re.search(name + r"=([^ |]+)", line)
    return m.group(1) if m else "?"


def summarise(path):
    arrivals, counts, terms, parked = [], [], [], 0
    wait_p95 = {}      # class -> max p95 ms seen
    wait_inflight = {}  # class -> max inflight seen
    http_mb = 0.0
    first_arrival_t = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for raw in f:
            line = raw.rstrip("\n")
            if "[Arrival]" in line:
                arrivals.append(line)
            elif "[ArrivalCount]" in line:
                counts.append(line)
            elif "[VramTerms]" in line:
                terms.append(line)
            elif "parked outfit unloaded under VRAM pressure" in line:
                parked += 1
            elif "[TexHttp]" in line:
                m = re.search(r"MB=" + NUM, line)
                if m:
                    http_mb = max(http_mb, float(m.group(1)))
            else:
                m = re.search(r"\[TexWait(?:\.(\w+))?\].*?inflight=(\d+).*?p95=(\d+)ms", line)
                if m:
                    cls = m.group(1) or "ALL"
                    wait_inflight[cls] = max(wait_inflight.get(cls, 0), int(m.group(2)))
                    wait_p95[cls] = max(wait_p95.get(cls, 0), int(m.group(3)))

    print(f"=== {path}")
    if not arrivals:
        print("  no [Arrival] line (client not started with -Diag, or the run ended before the first region finished)")
    for line in arrivals:
        print("  region      :", field(line, "region") if "region=" in line else "?",
              "| ended:", line.split("ended:")[-1].strip())
        for key in ("bakes", "noFaceWithoutTexture", "texInflight0"):
            print(f"  {key:22}: {field(line, key)}")
    for line in counts:
        print("  ", re.sub(r"^\[ArrivalCount\] t=[0-9.]+ ", "", line))
    print(f"  texture MB over HTTP (whole run): {http_mb:.1f}")
    if wait_p95:
        print("  [TexWait] worst p95 / peak open, per class:")
        for cls in sorted(wait_p95):
            print(f"    {cls:10} p95 {wait_p95[cls]/1000:7.1f} s   open {wait_inflight[cls]}")
    if terms:
        budgets = [int(re.search(r"budget=(\d+)MB", t).group(1)) for t in terms]
        print(f"  [VramTerms]: {len(terms)} changes, budget first {budgets[0]} / lowest {min(budgets)} / last {budgets[-1]} MB")
    print(f"  'parked outfit unloaded under VRAM pressure': {parked}")


if __name__ == "__main__":
    for p in sys.argv[1:]:
        summarise(p)
