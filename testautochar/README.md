# testautochar

TEST WORLDS ONLY. Server-side mod that skips the first-join character creation dialog (and the game pause it causes).

A Harmony prefix on `CharacterSystem.Event_PlayerJoinServer` sets the `createCharacter` moddata flag before vanilla runs, so the server reports DidSelect=true and the client never opens GuiDialogCreateCharacter. A postfix assigns the default class (characterClasses[0]) because vanilla only does that when the flag is unset.

Config `ModConfig/testautochar.json`: `{ "Enabled": true, "CharacterClass": "" }`. Empty class = default; a valid class code is applied via `CharacterSystem.setCharacterClass`.

Logs one line per join: `[testautochar] marked createCharacter for <name>`.

Build: `dotnet build -c Release` (net10.0, VINTAGE_STORY defaults to %APPDATA%\Vintagestory). Zip modinfo.json + testautochar.dll.
