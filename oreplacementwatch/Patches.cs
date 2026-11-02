using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace OrePlacementWatch
{
    /// <summary>
    /// Describes the GenDeposit call currently running on this thread. Set by the GenDeposit prefixes,
    /// cleared by their finalizers, read by the TryPlaceBlockForWorldGen prefix.
    /// </summary>
    internal sealed class DepositContext
    {
        public string Method;
        public int ChunkX, ChunkZ;
        public string Deposit;
        public BlockPos Pos;
        public object Generator;
        public DepositContext Previous;

        public string Describe()
        {
            string pos = Pos == null ? "?" : Pos.X + "," + Pos.Y + "," + Pos.Z;
            return "method=" + Method + " chunk=(" + ChunkX + "," + ChunkZ + ") depositPos=(" + pos + ") deposit=" + (Deposit ?? "?");
        }
    }

    internal static class Guard
    {
        [ThreadStatic] private static DepositContext current;

        public static DepositContext Current => current;

        public static DepositContext Enter(string method, object generator, DepositVariant variant, int chunkX, int chunkZ, BlockPos pos)
        {
            var ctx = new DepositContext
            {
                Method = method,
                Generator = generator,
                Deposit = variant?.Code ?? variant?.fromFile,
                ChunkX = chunkX,
                ChunkZ = chunkZ,
                Pos = pos?.Copy(),
                Previous = current
            };
            current = ctx;
            return ctx;
        }

        public static void Skip(DepositContext ctx, SkipKind kind, string rock, string index, string reason)
        {
            WatchLog.CountSkip(kind);
            WatchLog.Event("action=SKIPPED kind=" + kind + " " + ctx.Describe() + " rock=" + (rock ?? "?") + " index=" + (index ?? "?") + " reason=" + reason);
        }

        /// <summary>
        /// Shared Harmony finalizer body: restores the thread context and swallows the exception
        /// thrown by the original GenDeposit, so only that one deposit placement is lost.
        /// </summary>
        public static Exception Finish(Exception exception, DepositContext ctx, string method)
        {
            if (ctx != null) current = ctx.Previous;
            if (exception == null) return null;
            if (exception is OutOfMemoryException || exception is ThreadInterruptedException) return exception;

            WatchLog.CountException();
            string where = ctx != null ? ctx.Describe() : "method=" + method;
            string hazards = ctx?.Generator != null ? Hazards.Summary(ctx.Generator) : null;
            string frame = exception.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "?";
            WatchLog.Event("action=EXCEPTION_SUPPRESSED " + where
                + " rock=" + (hazards != null ? "see hazards" : "?") + " index=?"
                + " exception=" + exception.GetType().Name + ": " + exception.Message.Replace('\n', ' ')
                + " at=" + frame
                + (hazards != null ? " hazards=[" + hazards + "]" : ""));
            return null;
        }

        public static string CheckChunks(IServerChunk[] chunks, int worldheight)
        {
            if (chunks == null || chunks.Length == 0) return "chunk column array is null or empty";
            for (int i = 0; i < chunks.Length; i++)
            {
                if (chunks[i] == null) return "chunk " + i + " of the column is null";
            }
            if (chunks[0].MapChunk == null) return "map chunk of the column is null";
            if (worldheight > 0 && chunks.Length * GlobalConstants.ChunkSize < worldheight)
            {
                return "column has " + chunks.Length + " chunks but world height " + worldheight + " needs " + ((worldheight + GlobalConstants.ChunkSize - 1) / GlobalConstants.ChunkSize);
            }
            return null;
        }

        public static string RockOf(int blockId)
        {
            Block block = OrePlacementWatchModSystem.Sapi?.World.GetBlock(blockId);
            if (block == null) return "blockid:" + blockId;
            string rock = block.Variant != null && block.Variant.TryGetValue("rock", out string r) ? r : null;
            return rock != null ? rock + "(" + block.Code + ")" : block.Code?.ToString() ?? "blockid:" + blockId;
        }

        public static void InternalError(string where, Exception e)
        {
            WatchLog.Info("INTERNAL_ERROR in " + where + " (original method ran unchanged): " + e);
        }
    }

    /// <summary>
    /// Once per generator instance, scans its resolved per-rock ore-block lists and writes a HAZARD line
    /// listing every rock type whose list is too short (the root cause of the index crash).
    /// </summary>
    internal static class Hazards
    {
        private static readonly ConditionalWeakTable<object, string> summaries = new ConditionalWeakTable<object, string>();
        private const string None = "";

        public static void Note(object generator, DepositVariant variant, Dictionary<int, ResolvedDepositBlock> place, Dictionary<int, ResolvedDepositBlock> surface, int placeNeeded)
        {
            if (summaries.TryGetValue(generator, out _)) return;

            var problems = new List<string>();
            if (place != null)
            {
                foreach (var kv in place)
                {
                    int len = kv.Value?.Blocks?.Length ?? 0;
                    if (placeNeeded > 0 && len < placeNeeded)
                    {
                        problems.Add(Guard.RockOf(kv.Key) + " placeBlocks=" + len + "/" + placeNeeded);
                    }
                    if (surface != null)
                    {
                        if (!surface.TryGetValue(kv.Key, out var s)) problems.Add(Guard.RockOf(kv.Key) + " surfaceBlock=missing");
                        else if ((s?.Blocks?.Length ?? 0) == 0) problems.Add(Guard.RockOf(kv.Key) + " surfaceBlocks=0");
                    }
                }
            }

            string summary = problems.Count == 0 ? None : string.Join("; ", problems);
            try
            {
                summaries.Add(generator, summary);
            }
            catch (ArgumentException)
            {
                return; // another thread got there first
            }
            if (problems.Count > 0)
            {
                WatchLog.Info("HAZARD deposit=" + (variant?.Code ?? "?") + " file=" + (variant?.fromFile ?? "?") + " generator=" + generator.GetType().Name
                    + " incompleteRockTypes=" + problems.Count + " [" + summary + "]");
            }
        }

        public static string Summary(object generator)
        {
            if (!summaries.TryGetValue(generator, out string s) || s == None) return null;
            return s.Length > 600 ? s.Substring(0, 600) + "..." : s;
        }
    }

    /// <summary>
    /// DiscDepositGenerator.GenDeposit - used by every disc-type ore deposit (Anywhere / FollowSurface /
    /// FollowSurfaceBelow / FollowSealevel generators inherit it without overriding).
    /// Its ore index is already clamped by the game; the per-rock surface-block lookup
    /// (surfaceBlockByInBlockId[rock].Blocks[0]) is not, and is caught by the finalizer.
    /// </summary>
    [HarmonyPatch(typeof(DiscDepositGenerator), nameof(DiscDepositGenerator.GenDeposit))]
    internal static class DiscGenDepositPatch
    {
        private const string Method = "DiscDepositGenerator.GenDeposit";

        [HarmonyPrefix]
        private static bool Prefix(DiscDepositGenerator __instance, IServerChunk[] chunks, int chunkX, int chunkZ, BlockPos depoCenterPos,
            Dictionary<int, ResolvedDepositBlock> ___placeBlockByInBlockId, Dictionary<int, ResolvedDepositBlock> ___surfaceBlockByInBlockId,
            DepositVariant ___variant, int ___worldheight, out DepositContext __state)
        {
            __state = null;
            try
            {
                var ctx = Guard.Enter(Method + "[" + __instance.GetType().Name + "]", __instance, ___variant, chunkX, chunkZ, depoCenterPos);
                __state = ctx;

                string bad = Guard.CheckChunks(chunks, ___worldheight)
                    ?? (depoCenterPos == null ? "deposit position is null" : null)
                    ?? (__instance.PlaceBlock == null ? "PlaceBlock is null" : null)
                    ?? (__instance.Thickness == null ? "Thickness is null" : null)
                    ?? (__instance.Radius == null ? "Radius is null" : null);
                if (bad != null)
                {
                    Guard.Skip(ctx, SkipKind.InvalidInput, null, null, bad);
                    return false;
                }

                Hazards.Note(__instance, ___variant, ___placeBlockByInBlockId, __instance.SurfaceBlock != null ? ___surfaceBlockByInBlockId : null, 0);
            }
            catch (Exception e)
            {
                Guard.InternalError(Method + " prefix", e);
            }
            return true;
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, DepositContext __state) => Guard.Finish(__exception, __state, Method);
    }

    /// <summary>
    /// ChildDepositGenerator.GenDeposit - places child ores (e.g. gold/silver in quartz) around parent ore
    /// blocks. Indexes the per-rock ore-block list with an unclamped random grade index
    /// (value.Blocks[num2], num2 in [0, PlaceBlock.AllowedVariants.Length)), which throws
    /// IndexOutOfRangeException when an added rock type resolved fewer ore blocks than there are grades.
    /// </summary>
    [HarmonyPatch(typeof(ChildDepositGenerator), nameof(ChildDepositGenerator.GenDeposit))]
    internal static class ChildGenDepositPatch
    {
        private const string Method = "ChildDepositGenerator.GenDeposit";

        [HarmonyPrefix]
        private static bool Prefix(ChildDepositGenerator __instance, IServerChunk[] chunks, int originChunkX, int originChunkZ, BlockPos pos,
            Dictionary<int, ResolvedDepositBlock> ___placeBlockByInBlockId, Dictionary<int, ResolvedDepositBlock> ___surfaceBlockByInBlockId,
            DepositVariant ___variant, int ___worldheight, out DepositContext __state)
        {
            __state = null;
            try
            {
                var ctx = Guard.Enter(Method, __instance, ___variant, originChunkX, originChunkZ, pos);
                __state = ctx;

                string bad = Guard.CheckChunks(chunks, ___worldheight)
                    ?? (pos == null ? "deposit position is null" : null)
                    ?? (__instance.PlaceBlock == null ? "PlaceBlock is null" : null)
                    ?? (__instance.Radius == null ? "Radius is null" : null)
                    ?? (__instance.RandomTries == null ? "RandomTries is null" : null)
                    ?? (__instance.DepositRand == null ? "DepositRand is null" : null);
                if (bad != null)
                {
                    Guard.Skip(ctx, SkipKind.InvalidInput, null, null, bad);
                    return false;
                }

                string[] grades = __instance.PlaceBlock.AllowedVariants;
                int needed = grades != null ? grades.Length : 1;
                Hazards.Note(__instance, ___variant, ___placeBlockByInBlockId, __instance.SurfaceBlock != null ? ___surfaceBlockByInBlockId : null, needed);

                // Vanilla always passes a centre inside the column being generated; anything else would
                // write ore into wrapped coordinates of the wrong chunk.
                int size = GlobalConstants.ChunkSize;
                int lx = pos.X - originChunkX * size;
                int lz = pos.Z - originChunkZ * size;
                if (lx < 0 || lx >= size || lz < 0 || lz >= size || pos.Y <= 1 || pos.Y >= ___worldheight)
                {
                    Guard.Skip(ctx, SkipKind.OutsideGeneratingChunks, null, null, "child deposit centre is outside the generating chunk column");
                    return false;
                }

                // Predict the grade index the original will draw. LCGRandom's whole state is the public
                // currentSeed, so replay the same draws and restore it: the original then runs with the
                // exact same random sequence as without this mod.
                LCGRandom rand = __instance.DepositRand;
                long savedSeed = rand.currentSeed;
                int radius, gradeIndex;
                try
                {
                    radius = Math.Min(64, (int)__instance.Radius.nextFloat(1f, rand));
                    gradeIndex = radius > 0 && grades != null ? rand.NextInt(grades.Length) : 0;
                }
                finally
                {
                    rand.currentSeed = savedSeed;
                }
                if (radius <= 0) return true; // original returns without placing anything

                int index3d = (pos.Y % size * size + lz) * size + lx;
                int hostId = chunks[pos.Y / size].Data.GetBlockIdUnsafe(index3d);
                if (___placeBlockByInBlockId.TryGetValue(hostId, out ResolvedDepositBlock list))
                {
                    int len = list?.Blocks?.Length ?? 0;
                    if (gradeIndex >= len)
                    {
                        Guard.Skip(ctx, SkipKind.IndexOutOfBounds, Guard.RockOf(hostId), gradeIndex + "/" + len,
                            "ore-block list for this rock type has " + len + " entries, grade index " + gradeIndex + " would be out of bounds");
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                Guard.InternalError(Method + " prefix", e);
            }
            return true;
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, DepositContext __state) => Guard.Finish(__exception, __state, Method);
    }

    /// <summary>
    /// AlluvialDepositGenerator.GenDeposit - gravel/sand alluvial deposits keyed by the top rock type.
    /// Only basic input checks plus the exception finalizer.
    /// </summary>
    [HarmonyPatch(typeof(AlluvialDepositGenerator), nameof(AlluvialDepositGenerator.GenDeposit))]
    internal static class AlluvialGenDepositPatch
    {
        private const string Method = "AlluvialDepositGenerator.GenDeposit";

        [HarmonyPrefix]
        private static bool Prefix(AlluvialDepositGenerator __instance, IServerChunk[] chunks, int chunkX, int chunkZ, BlockPos depoCenterPos,
            DepositVariant ___variant, out DepositContext __state)
        {
            __state = null;
            try
            {
                var ctx = Guard.Enter(Method, __instance, ___variant, chunkX, chunkZ, depoCenterPos);
                __state = ctx;

                string bad = Guard.CheckChunks(chunks, __instance.Api?.World?.BlockAccessor?.MapSizeY ?? 0)
                    ?? (depoCenterPos == null ? "deposit position is null" : null);
                if (bad != null)
                {
                    Guard.Skip(ctx, SkipKind.InvalidInput, null, null, bad);
                    return false;
                }
            }
            catch (Exception e)
            {
                Guard.InternalError(Method + " prefix", e);
            }
            return true;
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, DepositContext __state) => Guard.Finish(__exception, __state, Method);
    }

    /// <summary>
    /// Block.TryPlaceBlockForWorldGen (base implementation, which BlockOre uses). Deposits with
    /// WithBlockCallback place through the worldgen block accessor at the real world position; child
    /// deposits can pick positions in a neighbouring column, which logs
    /// "Tried to get/set block outside generating chunks!". Only acts while a patched GenDeposit is
    /// running on this thread; everywhere else it returns immediately.
    /// </summary>
    [HarmonyPatch(typeof(Block), nameof(Block.TryPlaceBlockForWorldGen))]
    internal static class TryPlaceBlockForWorldGenPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Block __instance, IBlockAccessor blockAccessor, BlockPos pos, ref bool __result)
        {
            DepositContext ctx = Guard.Current;
            if (ctx == null) return true;

            try
            {
                string why = null;
                if (blockAccessor == null || pos == null)
                {
                    why = "block accessor or position is null";
                }
                else if (pos.Y < 0 || pos.Y >= blockAccessor.MapSizeY)
                {
                    why = "target Y " + pos.Y + " is outside the world";
                }
                else if (blockAccessor.GetChunkAtBlockPos(pos) == null
                    && OrePlacementWatchModSystem.Sapi?.World.BlockAccessor.GetChunkAtBlockPos(pos) == null)
                {
                    // Same two lookups BlockAccessorWorldGen.GetBlockId/SetBlock do before printing the message
                    why = "target (" + pos.X + "," + pos.Y + "," + pos.Z + ") chunk=(" + pos.X / GlobalConstants.ChunkSize + "," + pos.Y / GlobalConstants.ChunkSize + "," + pos.Z / GlobalConstants.ChunkSize
                        + ") is outside the generating chunks and not loaded";
                }
                if (why == null) return true;

                Guard.Skip(ctx, SkipKind.OutsideGeneratingChunks, __instance.Code?.ToString(), null, why);
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                Guard.InternalError("Block.TryPlaceBlockForWorldGen prefix", e);
                return true;
            }
        }
    }
}
