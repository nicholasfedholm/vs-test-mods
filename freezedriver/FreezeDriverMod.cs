#pragma warning disable CS0618
// FreezeDriver: scripted active-play driver for the freeze hunt. Loaded only via --addModPath, never installed to Mods.
// Env: FREEZEDRIVER_SCRIPT = timeline json, FREEZEDRIVER_OUT = output folder (driver.jsonl, stall.flag, commands_runtime.txt).
// Everything the timeline does on the server goes through chat commands, built-in ones or /fdrive below.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace FreezeDriver
{
    // Client and server instances of the mod may not share statics, so the shared state lives in AppDomain data as BCL types.
    public sealed class Bus
    {
        public Action<string> Line = _ => { };
        public long[] Beats = new long[2];   // 0 client frame, 1 server tick: Stopwatch timestamps
        public string[] Step = { "load" };
        public string OutDir = "";

        public static Bus Get()
        {
            lock (AppDomain.CurrentDomain)
            {
                var b = new Bus();
                b.OutDir = Environment.GetEnvironmentVariable("FREEZEDRIVER_OUT") ?? ".";
                if (AppDomain.CurrentDomain.GetData("freezedriver.bus") is object[] o)
                {
                    b.Line = (Action<string>)o[0]; b.Beats = (long[])o[1]; b.Step = (string[])o[2];
                    return b;
                }
                Directory.CreateDirectory(b.OutDir);
                var q = new BlockingCollection<string>();
                var w = new StreamWriter(Path.Combine(b.OutDir, "driver.jsonl"), true) { AutoFlush = true };
                var writer = new Thread(() => { foreach (var s in q.GetConsumingEnumerable()) { try { w.WriteLine(s); } catch { } } }) { IsBackground = true, Name = "fdrive-writer" };
                writer.Start();
                b.Line = s => q.Add(s);
                AppDomain.CurrentDomain.SetData("freezedriver.bus", new object[] { b.Line, b.Beats, b.Step });
                b.StartWatchdog();
                return b;
            }
        }

        public void Emit(JObject o)
        {
            o["t"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            o["step"] = Step[0];
            Line(o.ToString(Newtonsoft.Json.Formatting.None));
        }

        public static long Ms(long ticks) => (long)(ticks * 1000.0 / Stopwatch.Frequency);

        // A managed thread that only sleeps. If it stalls, the whole process was stopped (GC or CPU starvation).
        // If it keeps running while a heartbeat ages, that one thread is stuck, and stall.flag asks the runner for a dotnet-stack.
        void StartWatchdog()
        {
            var t = new Thread(() =>
            {
                long last = Stopwatch.GetTimestamp();
                double pauseLast = GC.GetTotalPauseDuration().TotalMilliseconds;
                int g2Last = GC.CollectionCount(2);
                bool[] flagged = new bool[2];
                long[] lastFlag = new long[2];
                long graceUntil = 0;
                string[] side = { "client", "server" };
                while (true)
                {
                    Thread.Sleep(10);
                    long now = Stopwatch.GetTimestamp();
                    long gap = Ms(now - last);
                    double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
                    int g2 = GC.CollectionCount(2);
                    if (gap >= 100)
                    {
                        Emit(new JObject { ["type"] = "stall", ["side"] = "process", ["ms"] = gap, ["gc_pause_ms"] = Math.Round(pause - pauseLast), ["gc2"] = g2 - g2Last });
                        graceUntil = now + Stopwatch.Frequency / 3; // heartbeats look old right after a whole-process stop
                    }
                    last = now; pauseLast = pause; g2Last = g2;
                    if (now < graceUntil) continue;
                    for (int i = 0; i < 2; i++)
                    {
                        long beat = Volatile.Read(ref Beats[i]);
                        if (beat == 0) continue;
                        long age = Ms(now - beat);
                        if (age < 700) { flagged[i] = false; continue; }
                        if (!flagged[i]) Emit(new JObject { ["type"] = "stall_live", ["side"] = side[i], ["age_ms"] = age });
                        if (!flagged[i] || Ms(now - lastFlag[i]) >= 1500)
                        {
                            lastFlag[i] = now;
                            try { File.AppendAllText(Path.Combine(OutDir, "stall.flag"), side[i] + "\n"); } catch { }
                        }
                        flagged[i] = true;
                    }
                }
            }) { IsBackground = true, Name = "fdrive-watchdog", Priority = ThreadPriority.AboveNormal };
            t.Start();
        }
    }

    public class FreezeDriverMod : ModSystem
    {
        Bus bus = null!;
        ICoreServerAPI sapi = null!;
        readonly List<Entity> spawned = new();
        double lastPauseServer; long lastTick; bool joined;
        long digListener; int digLeft; int digDone; int digPerTick; int digI; BlockPos? digCenter; IServerPlayer? digPlayer;

        public override bool ShouldLoad(EnumAppSide forSide) => Environment.GetEnvironmentVariable("FREEZEDRIVER_SCRIPT") != null;

        public override void StartPre(ICoreAPI api) { bus = Bus.Get(); }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            bus.Emit(new JObject { ["type"] = "info", ["kind"] = "server_start" });
            api.Event.PlayerNowPlaying += p =>
            {
                p.WorldData.CurrentGameMode = EnumGameMode.Creative;
                p.WorldData.FreeMove = true;
                p.WorldData.NoClip = true;
                p.BroadcastPlayerData(true);
                joined = true;
                bus.Emit(new JObject { ["type"] = "info", ["kind"] = "join", ["x"] = Math.Round(p.Entity.Pos.X), ["y"] = Math.Round(p.Entity.Pos.Y), ["z"] = Math.Round(p.Entity.Pos.Z), ["map_x"] = api.WorldManager.MapSizeX, ["map_z"] = api.WorldManager.MapSizeZ, ["sea"] = api.World.SeaLevel, ["app_name"] = AppDomain.CurrentDomain.FriendlyName, ["startup_hooks"] = AppContext.GetData("STARTUP_HOOKS")?.ToString() ?? "", ["gc_high_key"] = AppContext.GetData("System.GC.HighMemoryPercent")?.ToString() ?? "", ["gc_concurrent"] = AppContext.GetData("System.GC.Concurrent")?.ToString() ?? "" });
            };
            api.Event.GameWorldSave += () => bus.Emit(new JObject { ["type"] = "save", ["kind"] = "GameWorldSave fired" });
            api.Event.RegisterGameTickListener(_ =>
            {
                long now = Stopwatch.GetTimestamp();
                if (joined) Volatile.Write(ref bus.Beats[1], now);
                double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
                if (lastTick != 0)
                {
                    long gap = Bus.Ms(now - lastTick);
                    if (gap >= 250) bus.Emit(new JObject { ["type"] = "stall", ["side"] = "server", ["ms"] = gap, ["gc_pause_ms"] = Math.Round(pause - lastPauseServer) });
                }
                lastTick = now; lastPauseServer = pause;
            }, 20);
            api.Event.RegisterGameTickListener(_ =>
            {
                var i = GC.GetGCMemoryInfo();
                bus.Emit(new JObject
                {
                    ["type"] = "beat", ["side"] = "server",
                    ["entities"] = sapi.World.LoadedEntities.Count, ["chunks"] = sapi.WorldManager.AllLoadedChunks.Count,
                    ["gc_load_mb"] = i.MemoryLoadBytes / 1048576, ["gc_high_mb"] = i.HighMemoryLoadThresholdBytes / 1048576, ["gc_total_mb"] = i.TotalAvailableMemoryBytes / 1048576,
                    ["heap_mb"] = i.HeapSizeBytes / 1048576, ["committed_mb"] = i.TotalCommittedBytes / 1048576, ["gen"] = i.Generation,
                    ["gc_pause_total_ms"] = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds), ["gc2"] = GC.CollectionCount(2),
                });
            }, 5000);
            api.ChatCommands.Create("fdrive").WithDescription("freeze hunt driver").RequiresPrivilege(Privilege.chat)
                .WithArgs(api.ChatCommands.Parsers.All("args")).HandleWith(Handle);
        }

        TextCommandResult Handle(TextCommandCallingArgs a)
        {
            var parts = ((a[0] as string) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var p = a.Caller.Player as IServerPlayer;
            var sw = Stopwatch.StartNew();
            string sub = parts.Length > 0 ? parts[0] : "";
            var res = new JObject { ["type"] = "action", ["name"] = sub };
            try
            {
                int I(int n, int d) => parts.Length > n ? int.Parse(parts[n]) : d;
                string S(int n, string d) => parts.Length > n ? parts[n] : d;
                var c = p?.Entity.Pos.AsBlockPos ?? new BlockPos(0, 0, 0);
                var ba = sapi.World.BlockAccessor;
                switch (sub)
                {
                    case "tp":
                    {
                        // tp dx y dz: relative x and z, absolute y. A target within 1000 blocks of the map edge is mirrored.
                        int dx = I(1, 0), dz = I(3, 0), mx = sapi.WorldManager.MapSizeX, mz = sapi.WorldManager.MapSizeZ;
                        double x = p!.Entity.Pos.X, z = p.Entity.Pos.Z;
                        if (x + dx < 1000 || x + dx > mx - 1000) dx = -dx;
                        if (z + dz < 1000 || z + dz > mz - 1000) dz = -dz;
                        p.Entity.TeleportToDouble(x + dx, I(2, 170), z + dz);
                        res["to"] = new JArray(Math.Round(x + dx), I(2, 170), Math.Round(z + dz));
                        break;
                    }
                    case "clear":
                    case "fill":
                    {
                        // clear sx sy sz: box centred on the player, top at the terrain surface. fill also takes a block code.
                        int sx = I(1, 32), sy = I(2, 32), sz = I(3, 32);
                        int id = sub == "fill" ? sapi.World.GetBlock(new AssetLocation(S(4, "game:rock-granite")))!.Id : 0;
                        int top = ba.GetTerrainMapheightAt(c);
                        var bulk = sapi.World.GetBlockAccessorBulkUpdate(true, true);
                        var pos = new BlockPos(0, 0, 0);
                        for (int x = c.X - sx / 2; x < c.X + sx / 2; x++)
                            for (int z = c.Z - sz / 2; z < c.Z + sz / 2; z++)
                                for (int y = Math.Max(1, top - sy + 1); y <= top; y++) { pos.Set(x, y, z); bulk.SetBlock(id, pos); }
                        long set = sw.ElapsedMilliseconds;
                        var ups = bulk.Commit();
                        res["set_ms"] = set; res["updates"] = ups?.Count ?? -1; res["requested"] = (long)sx * sy * sz; res["top"] = top;
                        break;
                    }
                    case "dig":
                    {
                        // dig n perTick: BreakBlock with drops on the surface around the player, n blocks spread over server ticks.
                        digLeft = I(1, 500); digPerTick = I(2, 10); digDone = 0; digI = 0; digCenter = c.Copy(); digPlayer = p;
                        if (digListener != 0) sapi.Event.UnregisterGameTickListener(digListener);
                        digListener = sapi.Event.RegisterGameTickListener(DigTick, 50);
                        break;
                    }
                    case "drop":
                    {
                        // drop code qty n: n item stacks around the player
                        var loc = new AssetLocation(S(1, "game:stone-granite")); int qty = I(2, 16), n = I(3, 100);
                        var it = sapi.World.GetItem(loc); var bl = it == null ? sapi.World.GetBlock(loc) : null;
                        if (it == null && bl == null) { res["error"] = "unknown " + loc; break; }
                        var e = p!.Entity.Pos.XYZ;
                        for (int i = 0; i < n; i++)
                            sapi.World.SpawnItemEntity(it != null ? new ItemStack(it, qty) : new ItemStack(bl, qty), e.AddCopy(sapi.World.Rand.NextDouble() * 16 - 8, 1, sapi.World.Rand.NextDouble() * 16 - 8));
                        res["n"] = n;
                        break;
                    }
                    case "spawn":
                    {
                        // spawn code n radius: n entities around the player, 3 blocks above the surface
                        var type = sapi.World.GetEntityType(new AssetLocation(S(1, "game:wolf-eurasian-adult-male")));
                        if (type == null) { res["error"] = "unknown entity " + S(1, ""); break; }
                        int n = I(2, 50), r = I(3, 24), made = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double x = p!.Entity.Pos.X + sapi.World.Rand.NextDouble() * 2 * r - r, z = p.Entity.Pos.Z + sapi.World.Rand.NextDouble() * 2 * r - r;
                            double y = Math.Max(ba.GetTerrainMapheightAt(new BlockPos((int)x, 0, (int)z)), sapi.World.SeaLevel) + 3;
                            var e = sapi.World.ClassRegistry.CreateEntity(type);
                            e.Pos.SetPos(x, y, z); e.ServerPos.SetPos(x, y, z);
                            sapi.World.SpawnEntity(e); spawned.Add(e); made++;
                        }
                        res["made"] = made;
                        break;
                    }
                    case "tpa":
                        p!.Entity.TeleportToDouble(I(1, 0), I(2, 170), I(3, 0));
                        break;
                    case "spawnmix":
                    {
                        // spawnmix n radius: n creatures of random types from everything loaded (fixed seed), first sight of a type costs the client shapes and atlas space
                        int n = I(1, 100), r = I(2, 32), made = 0, failed = 0;
                        var types = sapi.World.EntityTypes.Where(t => t.Class != null && t.Class.Contains("EntityAgent") && !t.Code.Path.Contains("player")).OrderBy(t => t.Code.ToString()).ToList();
                        var rng = new Random(12345); var seen = new HashSet<string>();
                        for (int i = 0; i < n && types.Count > 0; i++)
                        {
                            var t = types[rng.Next(types.Count)];
                            try
                            {
                                double x = p!.Entity.Pos.X + rng.NextDouble() * 2 * r - r, z = p.Entity.Pos.Z + rng.NextDouble() * 2 * r - r;
                                double y = Math.Max(ba.GetTerrainMapheightAt(new BlockPos((int)x, 0, (int)z)), sapi.World.SeaLevel) + 3;
                                var e = sapi.World.ClassRegistry.CreateEntity(t);
                                e.Pos.SetPos(x, y, z); e.ServerPos.SetPos(x, y, z);
                                sapi.World.SpawnEntity(e); spawned.Add(e); made++; seen.Add(t.Code.ToString());
                            }
                            catch (Exception ex) { failed++; res["last_error"] = t.Code + ": " + ex.Message; }
                        }
                        res["types_total"] = types.Count; res["types_used"] = seen.Count; res["made"] = made; res["failed"] = failed;
                        break;
                    }
                    case "killspawned":
                    {
                        int n = 0;
                        foreach (var e in spawned) if (e.Alive) { e.Die(EnumDespawnReason.Removed); n++; }
                        res["n"] = n; res["total"] = spawned.Count; spawned.Clear();
                        break;
                    }
                    case "dumpcmds":
                    {
                        var lines = new List<string>();
                        foreach (var kv in sapi.ChatCommands) lines.Add(kv.Key + "\t" + kv.Value.Description + "\t" + string.Join(",", kv.Value.AllSubcommands.Keys));
                        lines.Sort();
                        File.WriteAllLines(Path.Combine(bus.OutDir, "commands_runtime.txt"), lines);
                        res["n"] = lines.Count;
                        break;
                    }
                    default:
                        res["error"] = "unknown sub " + sub;
                        break;
                }
            }
            catch (Exception e) { res["error"] = e.GetType().Name + ": " + e.Message; }
            res["ms"] = sw.ElapsedMilliseconds;
            res["args"] = string.Join(" ", parts);
            bus.Emit(res);
            return TextCommandResult.Success("fdrive " + sub + " " + sw.ElapsedMilliseconds + " ms");
        }

        void DigTick(float dt)
        {
            if (digCenter == null || digLeft <= 0) { sapi.Event.UnregisterGameTickListener(digListener); digListener = 0; bus.Emit(new JObject { ["type"] = "action", ["name"] = "dig_done", ["n"] = digDone }); return; }
            var ba = sapi.World.BlockAccessor;
            for (int k = 0; k < digPerTick && digLeft > 0; k++)
            {
                // square spiral outward from the centre, breaks the top solid block of each column
                int s = (int)Math.Ceiling(Math.Sqrt(digI + 1)); digI++;
                int dx = digI % Math.Max(1, s) - s / 2, dz = (digI / Math.Max(1, s)) - s / 2;
                var pos = new BlockPos(digCenter.X + dx, 0, digCenter.Z + dz);
                pos.Y = ba.GetTerrainMapheightAt(pos);
                if (pos.Y > 0 && ba.GetBlock(pos).Id != 0) { ba.BreakBlock(pos, digPlayer); digDone++; }
                digLeft--;
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            var path = Environment.GetEnvironmentVariable("FREEZEDRIVER_SCRIPT")!;
            var eng = new Engine(api, bus, JObject.Parse(File.ReadAllText(path)));
            api.Event.RegisterRenderer(eng, EnumRenderStage.Before, "freezedriver");
            api.Event.LevelFinalize += eng.OnLevelFinalize;
            api.Event.ChatMessage += (g, msg, type, data) =>
            {
                bus.Emit(new JObject { ["type"] = "chat", ["msg"] = msg == null ? "" : (msg.Length > 240 ? msg.Substring(0, 240) : msg) });
            };
        }
    }

    public class Engine : IRenderer
    {
        readonly ICoreClientAPI capi; readonly Bus bus; readonly JArray steps; readonly double settleS;
        readonly Stopwatch clock = Stopwatch.StartNew();
        public double RenderOrder => 0; public int RenderRange => 0;
        int idx = -1; bool ready, finished; double stepEnd, readyAt = -1, lastFrame = -1, lastBeat, lastUnpause; double pauseLast;
        bool pin, seekLand; double x0, y0, z0, y1, vx, vz, flyT0, flyDur, lastSeek; float yaw, pitch; int ocean;

        public Engine(ICoreClientAPI c, Bus b, JObject script)
        {
            capi = c; bus = b; steps = (JArray)script["steps"]!; settleS = script["settle_s"]?.Value<double>() ?? 20;
        }

        public void OnLevelFinalize() { ready = true; bus.Emit(new JObject { ["type"] = "info", ["kind"] = "level_finalize" }); }
        public void Dispose() { }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            long ticks = Stopwatch.GetTimestamp();
            if (ready) Volatile.Write(ref bus.Beats[0], ticks);
            double now = clock.Elapsed.TotalSeconds, pause = GC.GetTotalPauseDuration().TotalMilliseconds;
            if (lastFrame >= 0 && now - lastFrame >= 0.1)
                bus.Emit(new JObject { ["type"] = "stall", ["side"] = "client", ["ms"] = Math.Round((now - lastFrame) * 1000), ["gc_pause_ms"] = Math.Round(pause - pauseLast) });
            lastFrame = now; pauseLast = pause;
            if (!ready) return;
            if (readyAt < 0) { readyAt = now; stepEnd = now + settleS; bus.Step[0] = "settle"; }
            if (now - lastUnpause > 0.5)
            {
                lastUnpause = now;
                try { if (capi.IsGamePaused) { capi.PauseGame(false); bus.Emit(new JObject { ["type"] = "info", ["kind"] = "unpause" }); } } catch { }
            }
            if (now - lastBeat >= 5)
            {
                lastBeat = now;
                var e0 = capi.World.Player?.Entity;
                if (e0 != null) bus.Emit(new JObject { ["type"] = "beat", ["side"] = "client", ["x"] = Math.Round(e0.Pos.X), ["y"] = Math.Round(e0.Pos.Y), ["z"] = Math.Round(e0.Pos.Z), ["top"] = TopAt(e0.Pos.X, e0.Pos.Z), ["guis"] = capi.OpenedGuis.Count(), ["paused"] = capi.IsGamePaused });
            }
            if (pin) Pin(now);
            if (!finished && now >= stepEnd) Next(now);
        }

        void Pin(double now)
        {
            var ent = capi.World.Player?.Entity;
            if (ent == null) return;
            double t = Math.Min(now - flyT0, flyDur), ramp = Math.Min(1, t / 3);
            double x = x0 + vx * t, y = y0 + (y1 - y0) * ramp, z = z0 + vz * t;
            try
            {
                ent.Pos.SetPos(x, y, z); ent.Pos.Motion.Set(0, 0, 0); ent.Pos.Yaw = yaw; ent.Pos.Pitch = pitch;
                var sp = ent.SidedPos; sp.SetPos(x, y, z); sp.Motion.Set(0, 0, 0); sp.Yaw = yaw; sp.Pitch = pitch;
                capi.Input.MouseYaw = yaw; capi.Input.MousePitch = pitch;
            }
            catch { }
            if (now - flyT0 >= flyDur) { pin = false; return; }
            // Stay over land like a real traveller, and inside the map: turn when the ground under the player is sea for two checks.
            if (!seekLand || now - lastSeek < 4) return;
            lastSeek = now;
            int top = TopAt(x, z);
            ocean = top > 0 && top < capi.World.SeaLevel - 3 ? ocean + 1 : 0;
            var ba = capi.World.BlockAccessor;
            bool edge = x < 1000 || z < 1000 || x > ba.MapSizeX - 1000 || z > ba.MapSizeZ - 1000;
            if (ocean < 2 && !edge) return;
            float turn = (float)((edge ? 180 : 60) * Math.PI / 180);
            yaw += turn;
            double speed = Math.Sqrt(vx * vx + vz * vz);
            x0 = x; y0 = y; z0 = z; y1 = y; flyDur -= t; flyT0 = now;
            vx = -Math.Sin(yaw) * speed; vz = -Math.Cos(yaw) * speed; ocean = 0;
            bus.Emit(new JObject { ["type"] = "info", ["kind"] = edge ? "turn_edge" : "turn_sea", ["x"] = Math.Round(x), ["z"] = Math.Round(z), ["top"] = top });
        }

        int TopAt(double x, double z)
        {
            try { return capi.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos((int)x, 0, (int)z)); } catch { return -1; }
        }

        void Next(double now)
        {
            idx++;
            if (idx >= steps.Count)
            {
                finished = true; bus.Step[0] = "end";
                bus.Emit(new JObject { ["type"] = "info", ["kind"] = "timeline_end" });
                return;
            }
            var s = (JObject)steps[idx];
            string kind = (string?)s["do"] ?? "wait";
            string label = (string?)s["label"] ?? kind;
            bus.Step[0] = idx + ":" + label;
            double wait = s["wait_s"]?.Value<double>() ?? 5;
            var ev = new JObject { ["type"] = "step", ["i"] = idx, ["do"] = kind, ["label"] = label };
            var sw = Stopwatch.StartNew();
            try
            {
                switch (kind)
                {
                    case "cmd": capi.SendChatMessage((string)s["cmd"]!); ev["cmd"] = s["cmd"]; break;
                    case "ccmd": capi.TriggerChatMessage((string)s["cmd"]!); ev["cmd"] = s["cmd"]; break;
                    case "fly":
                    {
                        var ent = capi.World.Player!.Entity;
                        double deg = s["yaw"]?.Value<double>() ?? 225, speed = s["speed"]?.Value<double>() ?? 20, dist = s["dist"]?.Value<double>() ?? 600;
                        yaw = (float)(deg * Math.PI / 180); pitch = (float)((s["pitch"]?.Value<double>() ?? -8) * Math.PI / 180);
                        x0 = ent.Pos.X; y0 = ent.Pos.Y; z0 = ent.Pos.Z; y1 = s["y"]?.Value<double>() ?? y0;
                        vx = -Math.Sin(yaw) * speed; vz = -Math.Cos(yaw) * speed; flyDur = dist / speed; flyT0 = now; pin = true;
                        seekLand = s["seek_land"]?.Value<bool>() ?? false; ocean = 0; lastSeek = now;
                        wait = flyDur; ev["from"] = new JArray(Math.Round(x0), Math.Round(y0), Math.Round(z0)); ev["speed"] = speed; ev["dist"] = dist;
                        break;
                    }
                    case "hotkey":
                    {
                        var code = (string)s["code"]!;
                        var hk = capi.Input.GetHotKeyByCode(code);
                        ev["code"] = code;
                        if (hk == null) ev["error"] = "no such hotkey";
                        else ev["handled"] = hk.Handler?.Invoke(hk.CurrentMapping) ?? false;
                        ev["guis"] = new JArray(capi.OpenedGuis.OfType<GuiDialog>().Where(g => g.DialogType == EnumDialogType.Dialog).Select(g => g.GetType().Name));
                        break;
                    }
                    case "close":
                    {
                        var closed = new JArray();
                        foreach (var g in capi.OpenedGuis.OfType<GuiDialog>().Where(g => g.DialogType == EnumDialogType.Dialog).ToList()) closed.Add(g.GetType().Name + ":" + g.TryClose());
                        ev["closed"] = closed;
                        break;
                    }
                    default: break;
                }
            }
            catch (Exception e) { ev["error"] = e.GetType().Name + ": " + e.Message; }
            ev["call_ms"] = sw.ElapsedMilliseconds;
            bus.Emit(ev);
            stepEnd = now + wait;
        }
    }
}
