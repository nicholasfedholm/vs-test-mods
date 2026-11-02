using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace OrePlacementWatch
{
    /// <summary>
    /// TEST-ONLY. Applies the Harmony patches in Patches.cs and registers /orewatch.
    /// Remove this mod once the ore-placement crash has been diagnosed.
    /// </summary>
    public class OrePlacementWatchModSystem : ModSystem
    {
        public const string HarmonyId = "oreplacementwatch";

        internal static ICoreServerAPI Sapi;

        private static readonly Type[] PatchClasses =
        {
            typeof(DiscGenDepositPatch),
            typeof(ChildGenDepositPatch),
            typeof(AlluvialGenDepositPatch),
            typeof(TryPlaceBlockForWorldGenPatch)
        };

        private Harmony harmony;
        private readonly List<string> failedPatches = new List<string>();

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        // Patch before any worldgen system initialises
        public override double ExecuteOrder() => 0;

        public override void StartServerSide(ICoreServerAPI api)
        {
            Sapi = api;
            WatchLog.Open();
            WatchLog.Info("OrePlacementWatch 1.0.0 (TEST-ONLY, remove after testing) started. Game " + GameVersionString());

            harmony = new Harmony(HarmonyId);
            foreach (Type patchClass in PatchClasses)
            {
                try
                {
                    harmony.CreateClassProcessor(patchClass).Patch();
                }
                catch (Exception e)
                {
                    failedPatches.Add(patchClass.Name + ": " + e.Message);
                    WatchLog.Info("PATCH_FAILED " + patchClass.Name + ": " + e);
                    api.Logger.Error("[OrePlacementWatch] Failed to apply {0}: {1}", patchClass.Name, e);
                }
            }

            foreach (string m in PatchedMethodNames())
            {
                WatchLog.Info("Patched " + m);
            }
            api.Logger.Notification("[OrePlacementWatch] TEST-ONLY diagnostic active, logging to {0}", WatchLog.FilePath);

            api.ChatCommands.Create("orewatch")
                .WithDescription("OrePlacementWatch (TEST-ONLY): ore placement diagnostics")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ => TextCommandResult.Success(StatusReport()))
                .BeginSubCommand("status")
                    .WithDescription("Skipped placements, suppressed exceptions, log file and patch state")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(_ => TextCommandResult.Success(StatusReport()))
                .EndSubCommand();
        }

        private static string GameVersionString() => Vintagestory.API.Config.GameVersion.ShortGameVersion;

        private IEnumerable<string> PatchedMethodNames()
        {
            if (harmony == null) return Enumerable.Empty<string>();
            return harmony.GetPatchedMethods()
                .Where(m => Harmony.GetPatchInfo(m)?.Owners.Contains(HarmonyId) == true)
                .Select(m => m.DeclaringType?.Name + "." + m.Name)
                .OrderBy(s => s);
        }

        private string StatusReport()
        {
            var patched = PatchedMethodNames().ToList();
            var sb = new StringBuilder();
            sb.AppendLine("OrePlacementWatch (TEST-ONLY - remove after testing)");
            sb.AppendLine("Patch active: " + (patched.Count == PatchClasses.Length ? "yes" : patched.Count == 0 ? "NO" : "partially") + " (" + patched.Count + "/" + PatchClasses.Length + ")");
            foreach (string m in patched) sb.AppendLine("  patched: " + m);
            foreach (string f in failedPatches) sb.AppendLine("  FAILED: " + f);
            sb.AppendLine("Out-of-bounds placements skipped: " + WatchLog.Skipped
                + " (rock-type index: " + WatchLog.SkippedIndex
                + ", outside generating chunks: " + WatchLog.SkippedOutsideChunks
                + ", invalid input: " + WatchLog.SkippedInvalidInput + ")");
            sb.AppendLine("Exceptions suppressed: " + WatchLog.ExceptionsSuppressed);
            sb.Append("Log file: " + (WatchLog.FilePath ?? "(not open)"));
            return sb.ToString();
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            if (Sapi != null)
            {
                WatchLog.Info("Shutting down. Skipped=" + WatchLog.Skipped + " ExceptionsSuppressed=" + WatchLog.ExceptionsSuppressed);
            }
            WatchLog.Close();
            Sapi = null;
        }
    }
}
