# Vintage Story test mods

Six small TEST-ONLY helper mods for Vintage Story 1.22.7, used while testing our other mods. They are diagnostics and cheat/test commands. Do not run them on a world you care about, and remove them after testing.

Author per modinfo: "Muse (built for Nick)" (DinoShowcase lists "Muse (for Nick)").

These are not on ModDB individually. They were staged alongside MoonshotCore and SparkVS.

Only OrePlacementWatch has its source here. For the other five, the source project was not located in the workspace; each folder holds the extracted modinfo.json only, and the compiled binaries are omitted.

### dinoshowcase

- Name: Dino Showcase, version 1.2.0, type code
- Authors: Muse (for Nick)
- Source status: source not located (modinfo only)
- Visual-audit lineup tool for the LoP dinosaur pack. /showcase <family|all> spawns every species of a family in labeled rows (adult M, F, M, F, baby) with AI tasks stripped so they stand still for inspection. /showcase list shows families, /showcase clear removes all showcase animals. Short family nicknames work (tyranno, carchar, hadro, mosa, ...). Test worlds only.

### oreplacementwatch

- Name: Ore Placement Watch (TEST-ONLY), version 1.0.0, type code
- Authors: Muse (built for Nick)
- Source status: full C# source included
- TEST-ONLY diagnostic - remove after testing. Harmony-patches the base-game ore deposit generators (VSEssentials DiscDepositGenerator / ChildDepositGenerator / AlluvialDepositGenerator .GenDeposit) to log and skip ore placements that would index past a rock type's ore-block list or touch blocks outside the generating chunks, and suppresses exceptions thrown during ore placement so worldgen does not crash. Events go to Logs/oreplacementwatch-<timestamp>.log. /orewatch status (controlserver) shows counters.

### profilertool

- Name: Profiler Tool, version 1.0.0, type code
- Authors: Muse (built for Nick)
- Source status: source not located (modinfo only)
- Simple /profiler chat command (controlserver): server tick time, entity counts, loaded chunks, players, memory. No UI, no config.

### testlogger

- Name: Test Logger, version 1.0.1, type code
- Authors: Muse (built for Nick)
- Source status: source not located (modinfo only)
- AUTO-STARTS logging on server start, no command needed. Clean one-line-per-event test log for mod testing: entity spawn/despawn/death, engine warnings/errors/fatals, chat, perf snapshots every 60s. Commands: /testlog start | stop | status | note <text>. Server-side, requires controlserver.

### testloggerharmony

- Name: Test Logger (Harmony, Test-Only), version 1.0.1, type code
- Authors: Muse (built for Nick)
- Source status: source not located (modinfo only)
- AUTO-STARTS logging on server start, no command needed. TEST-ONLY: Test Logger with Harmony patch on Entity.OnHurt to log non-fatal HURT / damage events. Uses Harmony. REMOVE AFTER TESTING. Do NOT use in a real playthrough. Uses /testlogh, coexists with plain TestLogger which uses /testlog. Commands: /testlogh start | stop | status | note <text>. Server-side, requires controlserver.

### testtoolbox

- Name: Test Toolbox, version 1.0.1, type code
- Authors: Muse (built for Nick)
- Source status: source not located (modinfo only)
- Cheat/test commands for mod testing (spawn at generation, set generation, instant grow, tame, heal, experimental generic rideable, diagnostics). TEST WORLDS ONLY. Bypasses progression.
