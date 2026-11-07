"""Summarise driver.jsonl from an active run. Usage: py -3 -I driver_report.py <out\\label> [stall ms for the table, default 500]"""
import collections, json, os, sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
out = sys.argv[1]
thr = float(sys.argv[2]) if len(sys.argv) > 2 else 500
rows = []
for line in open(os.path.join(out, "driver.jsonl"), encoding="utf-8", errors="replace"):
    try:
        rows.append(json.loads(line))
    except ValueError:
        pass
print("records", len(rows), "by type", dict(collections.Counter(r.get("type") for r in rows)))
print("--- info")
for r in rows:
    if r.get("type") == "info":
        print(r["t"][11:19], r.get("kind"), {k: v for k, v in r.items() if k in ("x", "y", "z")})
print("--- steps (call_ms = time inside the client call, errors shown)")
for r in rows:
    if r.get("type") == "step":
        extra = {k: r[k] for k in ("cmd", "code", "handled", "guis", "closed", "error", "from", "speed", "dist") if k in r}
        print(r["t"][11:19], r["i"], r["label"], r.get("call_ms"), extra)
print("--- actions (server side)")
for r in rows:
    if r.get("type") == "action":
        print(r["t"][11:19], r.get("name"), r.get("ms"), {k: v for k, v in r.items() if k in ("set_ms", "updates", "requested", "top", "n", "made", "error", "args")})
print("--- chat, first 40")
for r in [x for x in rows if x.get("type") == "chat"][:40]:
    print(r["t"][11:19], r["msg"][:150].replace("\n", " | "))
print("--- saves")
for r in rows:
    if r.get("type") == "save":
        print(r["t"][11:19], r.get("step"))
st = [r for r in rows if r.get("type") == "stall"]
print("--- stalls by side: n, sum s, max ms, n >= thr, n >= thr with gc share under 50 pct")
for side in ("process", "client", "server"):
    s = [r for r in st if r.get("side") == side]
    big = [r for r in s if r["ms"] >= thr]
    nongc = [r for r in big if r.get("gc_pause_ms", 0) < r["ms"] * 0.5]
    print(side, len(s), round(sum(r["ms"] for r in s) / 1000, 1), max((r["ms"] for r in s), default=0), len(big), len(nongc))
print(f"--- stalls >= {thr} ms: t side ms gc_pause_ms gc2 step")
for r in st:
    if r["ms"] >= thr:
        print(r["t"][11:23], r["side"], r["ms"], r.get("gc_pause_ms"), r.get("gc2", ""), r.get("step"))
live = [r for r in rows if r.get("type") == "stall_live"]
print("--- stall_live (one thread stuck while the watchdog ran):", len(live))
for r in live[:40]:
    print(r["t"][11:23], r["side"], r.get("age_ms"), r.get("step"))
b = [r for r in rows if r.get("type") == "beat" and r.get("side") == "server"]
if b:
    l = b[-1]
    print("--- last server beat", {k: l.get(k) for k in ("t", "entities", "chunks", "gc_load_mb", "gc_high_mb", "gc_total_mb", "heap_mb", "committed_mb", "gc_pause_total_ms", "gc2")})
    print("max entities", max(r["entities"] for r in b), "max chunks", max(r["chunks"] for r in b), "max gc_load_mb", max(r["gc_load_mb"] for r in b))
