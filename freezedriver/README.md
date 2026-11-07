# FreezeDriver

Test-only Vintage Story 1.22 mod. Loaded with --addModPath on a save copy, never installed in Mods. Runs a JSON timeline as a player (fly, teleport, block clear, dig, fill, spawn, GUI hotkeys, saves) and records stalls per thread, GC pause share, heartbeats and GC memory info to driver.jsonl.

scripts\active_run.py launches the game on a save copy with a dotnet-trace gc-collect session, samples memory at 1 Hz, takes dotnet-stack on live single-thread stalls, restores world-keyed client data, and deletes the copy. Set VINTAGE_STORY to the game folder to build.

