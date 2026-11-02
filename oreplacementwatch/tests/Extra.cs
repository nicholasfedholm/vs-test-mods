using System; using System.Collections.Generic; using System.Reflection; using System.Runtime.CompilerServices;
using Vintagestory.ServerMods; using Vintagestory.API.MathTools; using Vintagestory.API.Common; using Vintagestory.API.Server;
static class Extra {
  static object W(string p) => typeof(OrePlacementWatch.OrePlacementWatchModSystem).Assembly.GetType("OrePlacementWatch.WatchLog").GetProperty(p).GetValue(null);
  static void Set(object o, string f, object v) { for (var t = o.GetType(); t != null; t = t.BaseType) { var fi = t.GetField(f, BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly); if (fi != null) { fi.SetValue(o, v); return; } } throw new Exception(f); }
  public static void Run() {
    var chunks = new IServerChunk[] { Stub.Make<IServerChunk>(), Stub.Make<IServerChunk>(), Stub.Make<IServerChunk>() };
    var d = new Dictionary<BlockPos, DepositVariant>();
    // Finalizer path: Disc passes prefix, original throws NRE (DepositRand null)
    var disc = (AnywhereDiscGenerator)RuntimeHelpers.GetUninitializedObject(typeof(AnywhereDiscGenerator));
    disc.Radius = NatFloat.createUniform(5, 0); disc.Thickness = NatFloat.createUniform(2, 0); disc.PlaceBlock = new DepositBlock();
    try { disc.GenDeposit(null, chunks, 1, 2, new BlockPos(40, -99, 70), ref d); Console.WriteLine("disc: no exception escaped, exc=" + W("ExceptionsSuppressed")); }
    catch (Exception e) { Console.WriteLine("disc: ESCAPED " + e.GetType().Name); }
    // Child index prediction
    int skips = 0, runs = 0, mism = 0;
    for (int seed = 1; seed <= 200; seed++) {
      var c = (ChildDepositGenerator)RuntimeHelpers.GetUninitializedObject(typeof(ChildDepositGenerator));
      c.Radius = NatFloat.createUniform(4, 0); c.RandomTries = NatFloat.createUniform(0, 0);
      c.PlaceBlock = new DepositBlock { AllowedVariants = new[]{"poor","medium","rich","bountiful"} };
      var rand = new LCGRandom(seed); rand.InitPositionSeed(3, 4); c.DepositRand = rand;
      var blk = (Block)RuntimeHelpers.GetUninitializedObject(typeof(Block));
      Set(c, "placeBlockByInBlockId", new Dictionary<int, ResolvedDepositBlock> { [Stub.HostId] = new ResolvedDepositBlock { Blocks = new[]{ blk } } });
      Set(c, "surfaceBlockByInBlockId", new Dictionary<int, ResolvedDepositBlock>());
      Set(c, "worldheight", 96);
      // expected index from an identical RNG
      var r2 = new LCGRandom(seed); r2.InitPositionSeed(3, 4); r2.NextFloat(); int expect = r2.NextInt(4);
      long before = (long)W("SkippedIndex"); long seedBefore = rand.currentSeed;
      try { c.GenDeposit(null, chunks, 1, 2, new BlockPos(40, 50, 70), ref d); } catch (Exception e) { Console.WriteLine("child ESCAPED " + e.GetType().Name); }
      bool skipped = (long)W("SkippedIndex") > before;
      if (skipped) { skips++; if (rand.currentSeed != seedBefore) mism++; } else runs++;
      if (skipped != (expect >= 1)) mism++;
    }
    Console.WriteLine($"child: skipped={skips} ran={runs} mismatches={mism} (expect ~150/50, 0 mismatches)");
    Console.WriteLine("totals skipped=" + W("Skipped") + " exc=" + W("ExceptionsSuppressed"));
  }
}
