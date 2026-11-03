"""A/B table for the arrival runs of tools/baseline-run.ps1 (FEAT-PERF-32): one row per run, one block per baseline folder.

    python -I tools/ab_compare.py baseline/20261101-1745 baseline/<new-folder> [--window 120]

Reads <run>.log and <run>.perf.txt of every kaltN / warmN in each folder. Reads only; changes nothing. Times are seconds
after the region handshake (the log's own t= is seconds since the start of the client, which differs from run to run).

  geom50 / geom90   when 50 % / 90 % of the geometry applies (mesh.apply + primmesh.apply + sculpt.apply items, from [WorkCost])
  wave2             first [TexStage.Net] report with more than 1500 faces waiting there: the second wave of scenery requests
  settled           first [TexStage.Total] report after the last one with more than 50 faces waiting (arrival_summary.py's rule)
  fps / low1%       median of the [Perf] lines from the handshake to handshake + --window seconds
  wait scn / avt    waitP50Ms (mean of the 5 s medians, weighted by n) and the largest waitMaxMs of the scenery labels
                    (mesh.apply, primmesh.apply, sculpt.apply, visual.create, visual.reload, visual.update) and of the
                    avatar labels (avatar.*, controlavatar.*), in seconds. Only runs of v0.28.85+ have them, under --diag;
                    "-" otherwise.
"""
import glob
import os
import re
import statistics
import sys

SCENERY = ("mesh.apply", "primmesh.apply", "sculpt.apply", "visual.create", "visual.reload", "visual.update")
GEOMETRY = ("mesh.apply", "primmesh.apply", "sculpt.apply")


def num(text):
    return float(text.replace(",", "."))


def seconds(m):
    return int(m.group(1)) * 3600 + int(m.group(2)) * 60 + int(m.group(3))


def read_log(path):
    """handshake (s since client start), the Net faces and the Total faces per report time."""
    handshake, net, total = None, {}, {}
    if not os.path.exists(path):
        return handshake, net, total
    with open(path, "rb") as f:
        for raw in f:
            if raw.startswith(b"[TexStage.Total] t=") or raw.startswith(b"[TexStage.Net] t="):
                m = re.match(rb"\[TexStage\.(\w+)\] t=([\d.]+) n=\d+.* open=(\d+)(?: faces=(\d+))?", raw)
                if m:
                    (net if m.group(1) == b"Net" else total)[float(m.group(2))] = int(m.group(4) or 0)
            elif raw.startswith(b"[Arrival] ") and handshake is None:
                m = re.search(rb"handshakeAt=([\d.]+)s", raw)
                if m:
                    handshake = float(m.group(1))
    return handshake, net, total


def read_perf(path):
    """Everything from <run>.perf.txt, with the time as seconds since client start (anchored on the first [TexStage] t=)."""
    base = None
    perf, geometry, waits = [], {}, {}
    if not os.path.exists(path):
        return perf, geometry, waits
    with open(path, "rb") as f:
        for raw in f:
            line = raw.decode("utf8", "replace")
            m = re.match(r"(\d\d):(\d\d):(\d\d) (.*)", line)
            if not m:
                continue
            wall, body = seconds(m), m.group(4)
            if base is None:
                mm = re.match(r"\[TexStage\] t=([\d.]+)", body)
                if mm:
                    base = wall - float(mm.group(1))
                continue
            t = wall - base
            if body.startswith("[Perf] "):
                f1 = re.search(r"fps=(\d+) low1%=(\d+)", body)
                if f1:
                    perf.append((t, int(f1.group(1)), int(f1.group(2))))
            elif body.startswith("[WorkCost] "):
                mm = re.match(r"\[WorkCost\]\s+(\S+)\s+n=(\d+)", body)
                if not mm or "/" in mm.group(1):
                    continue
                label, n = mm.group(1), int(mm.group(2))
                if label in GEOMETRY:
                    bucket = int(t // 5 * 5)
                    geometry[bucket] = geometry.get(bucket, 0) + n
                w = re.search(r"waitP50Ms=\s*([\d.,]+) waitMaxMs=\s*([\d.,]+)", body)
                if w:
                    waits.setdefault(label, []).append((n, num(w.group(1)), num(w.group(2))))
    return perf, geometry, waits


def reach(geometry, fraction):
    total = sum(geometry.values())
    cum = 0
    for bucket in sorted(geometry):
        cum += geometry[bucket]
        if total and cum >= fraction * total:
            return bucket
    return None


def wait_summary(waits, labels):
    """(n-weighted mean of the 5 s medians, largest max), seconds, over the labels that match; None without samples."""
    n_all = mean_sum = 0.0
    biggest = 0.0
    for label, samples in waits.items():
        if not labels(label):
            continue
        for n, p50, mx in samples:
            n_all += n
            mean_sum += n * p50
            biggest = max(biggest, mx)
    if n_all == 0:
        return None
    return mean_sum / n_all / 1000.0, biggest / 1000.0


def rel(value, handshake):
    return "-" if value is None or handshake is None else f"{value - handshake:.0f}"


def analyse(folder, run, window):
    handshake, net, total = read_log(os.path.join(folder, run + ".log"))
    perf, geometry, waits = read_perf(os.path.join(folder, run + ".perf.txt"))

    wave2 = min((t for t in sorted(net) if net[t] > 1500), default=None)
    busy = [t for t in total if total[t] > 50]
    settled = min((t for t in sorted(total) if busy and t > max(busy)), default=None)

    g50, g90 = reach(geometry, 0.5), reach(geometry, 0.9)
    start = handshake if handshake is not None else 0.0
    inside = [(f, low) for t, f, low in perf if start <= t <= start + window]
    fps = statistics.median(f for f, _ in inside) if inside else None
    low = statistics.median(low for _, low in inside) if inside else None

    scn = wait_summary(waits, lambda l: l in SCENERY)
    avt = wait_summary(waits, lambda l: l.startswith(("avatar.", "controlavatar.")))
    face = wait_summary(waits, lambda l: l == "avatar.face_material")
    return {
        "run": run,
        "hs": "-" if handshake is None else f"{handshake:.1f}",
        "geom50": rel(g50, handshake), "geom90": rel(g90, handshake),
        "wave2": rel(wave2, handshake), "settled": rel(settled, handshake),
        "fps": "-" if fps is None else f"{fps:.0f}", "low1%": "-" if low is None else f"{low:.0f}",
        "wait scn p50/max": "-" if scn is None else f"{scn[0]:.1f}/{scn[1]:.1f}",
        "wait avt p50/max": "-" if avt is None else f"{avt[0]:.1f}/{avt[1]:.1f}",
        "wait face_mat p50/max": "-" if face is None else f"{face[0]:.1f}/{face[1]:.1f}",
    }


def read_info(folder):
    """The arm of a folder, from info.txt of tools/baseline-run.ps1: version, texture streaming, the BoM probes."""
    path = os.path.join(folder, "info.txt")
    if not os.path.exists(path):
        return ""
    keep = ("version", "texStream", "bomProbe")
    fields = []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            key, _, value = line.partition(":")
            if key.strip() in keep:
                fields.append(f"{key.strip()} {value.strip()}")
    return "; ".join(fields)


def main(argv):
    window = 120.0
    folders = []
    i = 0
    while i < len(argv):
        if argv[i] == "--window" and i + 1 < len(argv):
            window = float(argv[i + 1])
            i += 2
        else:
            folders.append(argv[i])
            i += 1
    if not folders:
        print(__doc__)
        return 2

    for folder in folders:
        runs = sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(folder, "*.log"))
                      if re.match(r"(kalt|warm)\d+\.log$", os.path.basename(p)))
        print(f"=== {folder} (seconds after the handshake; fps/low1% over the first {window:.0f} s)")
        info = read_info(folder)
        if info:
            print(f"    {info}")
        if not runs:
            print("  no kaltN.log / warmN.log here")
            continue
        rows = [analyse(folder, run, window) for run in runs]
        columns = list(rows[0])
        widths = {c: max(len(c), *(len(r[c]) for r in rows)) for c in columns}
        print("  " + "  ".join(c.ljust(widths[c]) for c in columns))
        for r in rows:
            print("  " + "  ".join(r[c].ljust(widths[c]) for c in columns))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
