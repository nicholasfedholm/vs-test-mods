"""Active-play capture: launch Optimum on a save copy with the FreezeDriver timeline, trace GC, sample memory at 1 Hz,
take dotnet-stack on live stalls, close the game, restore the world-keyed client data, delete the copy.
Usage: py -3 -I active_run.py <label> <minutes> <source save, no extension> <timeline.json> [dotnet-trace profile, default gc-collect]
Env vars pass through from the caller (for example DOTNET_GCHighMemPercent). Live Mods and the source save are not touched."""
import csv, ctypes, ctypes.wintypes as w, datetime as dt, hashlib, os, shutil, subprocess, sys, threading, time

HOME = os.path.expanduser("~")
ROOT = HOME + r"\vs_scratch\freeze-hunt-20261106"
EXE = HOME + r"\AppData\Local\Programs\Optimum\Vintagestory.exe"
DATA = HOME + r"\AppData\Roaming\VintagestoryData"
SAVES = DATA + r"\Saves"
LOCK = HOME + r"\vs_scratch\GAME_LOCK.txt"
TRACE = HOME + r"\.dotnet\tools\dotnet-trace.exe"
STACK = HOME + r"\.dotnet\tools\dotnet-stack.exe"
MODPATH = ROOT + r"\driver\build"
WORLD_ID = "f95ebf74-1c10-4421-a2e7-aef8f1bf4283"
label, minutes, source, timeline = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
profile = sys.argv[5] if len(sys.argv) > 5 else "gc-collect"
copy_name = "freezehunt_copy"
out = os.path.join(ROOT, "out", label)
os.makedirs(out, exist_ok=True)
log_file = open(os.path.join(out, "run.log"), "a", encoding="utf-8", buffering=1)


def now():
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def log(*a):
    print(now(), *a, file=log_file)


def game_pids():
    r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Vintagestory.exe", "/FO", "CSV", "/NH"], capture_output=True, text=True)
    return [int(x.split('","')[1]) for x in r.stdout.splitlines() if x.startswith('"Vintagestory')]


class MS(ctypes.Structure):
    _fields_ = [("dwLength", w.DWORD), ("dwMemoryLoad", w.DWORD), ("total", ctypes.c_ulonglong), ("avail", ctypes.c_ulonglong), ("totalPF", ctypes.c_ulonglong), ("availPF", ctypes.c_ulonglong), ("totalV", ctypes.c_ulonglong), ("availV", ctypes.c_ulonglong), ("x", ctypes.c_ulonglong)]


class PMC(ctypes.Structure):
    _fields_ = [("cb", w.DWORD), ("PageFaultCount", w.DWORD), ("PeakWS", ctypes.c_size_t), ("WS", ctypes.c_size_t), ("a", ctypes.c_size_t), ("b", ctypes.c_size_t), ("c", ctypes.c_size_t), ("d", ctypes.c_size_t), ("Pagefile", ctypes.c_size_t), ("PeakPagefile", ctypes.c_size_t), ("Private", ctypes.c_size_t)]


k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi
k32.OpenProcess.restype = w.HANDLE
stop = threading.Event()
state = {"handle": None, "pid": None}


def sampler():
    with open(os.path.join(out, "mem.csv"), "w", newline="") as h:
        c = csv.writer(h)
        c.writerow(["t", "mem_load_pct", "avail_mb", "commit_used_mb", "game_ws_mb", "game_private_mb", "page_faults"])
        while not stop.is_set():
            m = MS()
            m.dwLength = ctypes.sizeof(MS)
            k32.GlobalMemoryStatusEx(ctypes.byref(m))
            ws = pv = pf = ""
            if state["handle"]:
                p = PMC()
                p.cb = ctypes.sizeof(PMC)
                if psapi.GetProcessMemoryInfo(w.HANDLE(state["handle"]), ctypes.byref(p), p.cb):
                    ws, pv, pf = p.WS // 1048576, p.Private // 1048576, p.PageFaultCount
            c.writerow([now(), m.dwMemoryLoad, m.avail // 1048576, (m.totalPF - m.availPF) // 1048576, ws, pv, pf])
            h.flush()
            stop.wait(1.0)


def stacker():
    """Driver appends one line to stall.flag when one thread's heartbeat ages past 700 ms while the process is alive."""
    flag = os.path.join(out, "stall.flag")
    sdir = os.path.join(out, "stacks")
    os.makedirs(sdir, exist_ok=True)
    seen, taken, last = 0, 0, 0.0
    while not stop.is_set():
        stop.wait(0.1)
        if not os.path.exists(flag) or not state["pid"]:
            continue
        try:
            lines = open(flag, encoding="utf-8").read().splitlines()
        except OSError:
            continue
        if len(lines) <= seen:
            continue
        side = lines[-1].strip()
        seen = len(lines)
        if time.time() - last < 1.5 or taken >= 40:
            continue
        last = time.time()
        taken += 1
        name = os.path.join(sdir, f"{taken:02d}_{side}_{now().replace(':', '')}.txt")
        with open(name, "w", encoding="utf-8") as h:
            subprocess.run([STACK, "report", "-p", str(state["pid"])], stdin=subprocess.DEVNULL, stdout=h, stderr=subprocess.STDOUT, timeout=60)
        log("stack", name)


def sha(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def keyed_files():
    """Files the save copy can write outside Saves because it shares the source world's id, plus clientsettings."""
    r = [os.path.join(DATA, "clientsettings.json")]
    maps = os.path.join(DATA, "Maps")
    r += [os.path.join(maps, n) for n in os.listdir(maps) if n.startswith(WORLD_ID)]
    for base, _, files in os.walk(os.path.join(DATA, "ModData", WORLD_ID)):
        r += [os.path.join(base, n) for n in files]
    return [p for p in r if os.path.isfile(p)]


def scan():
    r = {}
    for top in ("Maps", "ModData"):
        for base, _, files in os.walk(os.path.join(DATA, top)):
            for n in files:
                p = os.path.join(base, n)
                try:
                    s = os.stat(p)
                    r[p] = (s.st_size, s.st_mtime_ns)
                except OSError:
                    pass
    return r


snap = os.path.join(out, "world_snapshot")


def snapshot():
    os.makedirs(snap, exist_ok=True)
    n, size = 0, 0
    for p in keyed_files():
        d = os.path.join(snap, os.path.relpath(p, DATA))
        os.makedirs(os.path.dirname(d), exist_ok=True)
        shutil.copy2(p, d)
        n += 1
        size += os.path.getsize(p)
    log("snapshot", n, "files", size // 1048576, "MB")


def restore():
    saved = {}
    for base, _, files in os.walk(snap):
        for n in files:
            d = os.path.join(base, n)
            saved[os.path.join(DATA, os.path.relpath(d, snap))] = d
    changed = new = 0
    for p in keyed_files():
        if p not in saved:
            os.remove(p)
            new += 1
    for p, d in saved.items():
        if not os.path.exists(p) or sha(p) != sha(d):
            shutil.copy2(d, p)
            changed += 1
    bad = [p for p, d in saved.items() if sha(p) != sha(d)]
    log("restore: changed", changed, "removed new", new, "mismatch after", len(bad))
    return not bad


if game_pids():
    sys.exit("REFUSED: Vintagestory.exe already running")
if os.path.exists(LOCK):
    sys.exit("REFUSED: GAME_LOCK.txt present: " + open(LOCK, encoding="utf-8", errors="replace").readline().strip())
for n in os.listdir(SAVES):
    if n.startswith(copy_name):
        sys.exit("REFUSED: stale " + n + " in Saves")
if os.path.exists(os.path.join(out, "gc.nettrace")):
    sys.exit("REFUSED: label already has gc.nettrace")
with open(LOCK, "w", encoding="utf-8") as h:
    h.write(f"freeze hunt active capture {label}, started {now()}\n")
before = scan()
snapshot()
threading.Thread(target=sampler, daemon=True).start()
threading.Thread(target=stacker, daemon=True).start()
game = None
launched = time.time()
try:
    for x in ("", "-wal", "-shm"):
        s = os.path.join(SAVES, source + ".vcdbs" + x)
        if os.path.exists(s):
            shutil.copy2(s, os.path.join(SAVES, copy_name + ".vcdbs" + x))
            log("copied", s)
    env = dict(os.environ, FREEZEDRIVER_SCRIPT=timeline, FREEZEDRIVER_OUT=out)
    game = subprocess.Popen([EXE, "--openWorld", copy_name, "--addModPath", MODPATH], cwd=os.path.dirname(EXE), env=env)
    state["handle"] = k32.OpenProcess(0x1000 | 0x0010, False, game.pid)
    state["pid"] = game.pid
    log("launched pid", game.pid, "handle", bool(state["handle"]))
    span = f"00:00:{minutes:02d}:00"
    with open(os.path.join(out, "trace.log"), "w", encoding="utf-8") as tl:
        for attempt in range(40):
            time.sleep(0.5)
            if game.poll() is not None:
                log("game exited early code", game.returncode)
                break
            r = subprocess.run([TRACE, "collect", "-p", str(game.pid), "--profile", profile, "--duration", span, "-o", os.path.join(out, "gc.nettrace")], stdin=subprocess.DEVNULL, stdout=tl, stderr=tl)
            log("dotnet-trace attempt", attempt, "exit", r.returncode)
            if r.returncode == 0 and os.path.exists(os.path.join(out, "gc.nettrace")):
                break
    log("trace done, game alive", game.poll() is None)
finally:
    state["pid"] = None
    if game and game.poll() is None:
        subprocess.run(["taskkill", "/PID", str(game.pid)], capture_output=True)
        try:
            game.wait(120)
            log("closed by WM_CLOSE, code", game.returncode)
        except subprocess.TimeoutExpired:
            subprocess.run(["taskkill", "/PID", str(game.pid), "/F"], capture_output=True)
            log("forced kill after 120 s")
    stop.set()
    time.sleep(1.5)
    left = game_pids()
    log("Vintagestory left after close", left)
    for pid in left:
        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
        log("killed leftover", pid)
    time.sleep(1)
    keep = os.path.join(out, "logs")
    os.makedirs(keep, exist_ok=True)
    for src in ("Logs\\client-main.log", "Logs\\server-main.log"):
        if os.path.exists(os.path.join(DATA, src)):
            shutil.copy2(os.path.join(DATA, src), keep)
    plog = os.path.join(DATA, "PerfLog")
    for n in os.listdir(plog):
        if os.path.getmtime(os.path.join(plog, n)) >= launched:
            shutil.copy2(os.path.join(plog, n), keep)
    ok = restore()
    after = scan()
    other = [p for p, v in after.items() if before.get(p) != v and WORLD_ID not in p]
    log("changed outside world id", len(other), other[:20])
    removed = 0
    for n in os.listdir(SAVES):
        if n.startswith(copy_name):
            os.remove(os.path.join(SAVES, n))
            removed += 1
    log("save copy files removed", removed)
    if ok:
        shutil.rmtree(snap, ignore_errors=True)
        log("snapshot deleted, restore verified")
    else:
        log("KEPT snapshot, restore mismatch", snap)
    if os.path.exists(LOCK):
        os.remove(LOCK)
    log("lock removed")
print("done", label, "see", out)
