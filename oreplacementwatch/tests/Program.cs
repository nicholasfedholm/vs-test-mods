using System; using System.IO; using System.Linq; using System.Reflection; using System.Collections.Generic; using System.Runtime.CompilerServices;
using HarmonyLib; using Vintagestory.ServerMods; using Vintagestory.API.MathTools; using Vintagestory.API.Common; using Vintagestory.API.Server;
class P {
  static void Main() {
    string vs = @"%APPDATA%\Vintagestory";
    AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
      string n = new AssemblyName(e.Name).Name + ".dll";
      foreach (var d in new[]{ vs, vs+@"\Lib", vs+@"\Mods", AppContext.BaseDirectory }) { var p = Path.Combine(d, n); if (File.Exists(p)) return Assembly.LoadFrom(p); }
      return null; };
    Run();
  }
  [MethodImpl(MethodImplOptions.NoInlining)]
  static void Run() {
    var asm = typeof(OrePlacementWatch.OrePlacementWatchModSystem).Assembly;
    var h = new Harmony("oreplacementwatch");
    foreach (var n in new[]{"DiscGenDepositPatch","ChildGenDepositPatch","AlluvialGenDepositPatch","TryPlaceBlockForWorldGenPatch"}) {
      try { h.CreateClassProcessor(asm.GetType("OrePlacementWatch."+n)).Patch(); Console.WriteLine("OK   " + n); }
      catch (Exception e) { Console.WriteLine("FAIL " + n + ": " + e.GetBaseException().Message); }
    }
    foreach (var m in h.GetPatchedMethods()) Console.WriteLine("patched " + m.DeclaringType.Name + "." + m.Name);
    // Disc with null chunks -> prefix should skip (no NRE)
    var disc = (AnywhereDiscGenerator)RuntimeHelpers.GetUninitializedObject(typeof(AnywhereDiscGenerator));
    var d = new Dictionary<BlockPos, DepositVariant>();
    disc.GenDeposit(null, null, 1, 2, new BlockPos(40, -99, 70), ref d);
    var child = (ChildDepositGenerator)RuntimeHelpers.GetUninitializedObject(typeof(ChildDepositGenerator));
    child.GenDeposit(null, null, 1, 2, new BlockPos(40, 50, 70), ref d);
    var w = asm.GetType("OrePlacementWatch.WatchLog");
    Console.WriteLine("skipped=" + w.GetProperty("Skipped").GetValue(null) + " invalid=" + w.GetProperty("SkippedInvalidInput").GetValue(null) + " exc=" + w.GetProperty("ExceptionsSuppressed").GetValue(null));
    // Block.TryPlaceBlockForWorldGen outside deposit context must be untouched
    Extra.Run();
    Console.WriteLine("done");
  }
}
