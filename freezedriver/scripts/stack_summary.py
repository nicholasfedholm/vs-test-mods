"""Top managed frames of the client main thread and server main thread in each dotnet-stack file.
Usage: py -3 -I stack_summary.py <out\\label\\stacks> [frames, default 10]"""
import glob, os, re, sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
n = int(sys.argv[2]) if len(sys.argv) > 2 else 10
MAIN = {"client": ("ClientProgram", "ClientPlatformWindows", "GameWindow", "ClientMain.MainGameLoop", "ClientMain.MainRenderLoop", "ScreenManager.Render"), "server": ("ServerMain.Process", "ServerMain.Launch", "ServerMain.Run", "ServerThread")}


def short(f):
    f = re.sub(r"^\S+!", "", f.strip())
    f = re.sub(r"\(.*$", "", f)
    return f[-90:]


for path in sorted(glob.glob(os.path.join(sys.argv[1], "*.txt"))):
    name = os.path.basename(path)
    side = name.split("_")[1]
    text = open(path, encoding="utf-8", errors="replace").read()
    threads = re.split(r"\n(?=Thread \(0x)", text)
    print("==", name, "threads", len(threads) - (0 if text.startswith("Thread") else 1))
    for want, keys in MAIN.items():
        for t in threads:
            lines = t.splitlines()
            body = [l for l in lines[1:] if l.strip()]
            if any(k in l for l in body for k in keys):
                print(" ", want, "*" if want == side else "", lines[0].strip())
                for l in body[:n]:
                    print("     ", short(l))
                break
