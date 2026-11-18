using System;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace TestAutoChar
{
    public class TestAutoCharConfig
    {
        public bool Enabled = true;
        // Optional class code (e.g. "commoner"). Empty = characterClasses[0].
        public string CharacterClass = "";
    }

    public class AutoChar : ModSystem
    {
        const string HarmonyId = "testautochar";
        static ICoreServerAPI sapi;
        static TestAutoCharConfig config = new TestAutoCharConfig();
        Harmony harmony;

        public override bool ShouldLoad(EnumAppSide side) { return side == EnumAppSide.Server; }

        public override double ExecuteOrder() { return 0.9; }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            try
            {
                config = api.LoadModConfig<TestAutoCharConfig>("testautochar.json");
                if (config == null)
                {
                    config = new TestAutoCharConfig();
                    api.StoreModConfig(config, "testautochar.json");
                }
            }
            catch (Exception e)
            {
                api.Logger.Warning("[testautochar] config load failed, using defaults: " + e.Message);
                config = new TestAutoCharConfig();
            }

            if (!config.Enabled)
            {
                api.Logger.Notification("[testautochar] disabled by config");
                return;
            }

            try
            {
                var target = AccessTools.Method(typeof(CharacterSystem), "Event_PlayerJoinServer");
                if (target == null)
                {
                    api.Logger.Error("[testautochar] Event_PlayerJoinServer not found, patch NOT applied");
                    return;
                }
                harmony = new Harmony(HarmonyId);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(AutoChar), nameof(JoinPrefix)),
                    postfix: new HarmonyMethod(typeof(AutoChar), nameof(JoinPostfix)));
                api.Logger.Notification("[testautochar] patched CharacterSystem.Event_PlayerJoinServer");
            }
            catch (Exception e) { api.Logger.Error("[testautochar] harmony patch failed: " + e); }
        }

        // Runs before vanilla: mark the character as already created so the client never opens the dialog.
        public static void JoinPrefix(IServerPlayer byPlayer)
        {
            try
            {
                byte[] cur = byPlayer.GetModdata("createCharacter");
                if (cur == null || !SerializerUtil.Deserialize<bool>(cur, false))
                {
                    byPlayer.SetModdata("createCharacter", SerializerUtil.Serialize(true));
                }
                sapi?.Logger.Notification("[testautochar] marked createCharacter for " + byPlayer.PlayerName);
            }
            catch (Exception e) { sapi?.Logger.Warning("[testautochar] prefix failed: " + e.Message); }
        }

        // Vanilla only assigns a default class when the flag is unset, so do it here if the entity has none.
        public static void JoinPostfix(IServerPlayer byPlayer)
        {
            try
            {
                var ent = byPlayer.Entity;
                var cs = sapi?.ModLoader.GetModSystem<CharacterSystem>();
                if (ent == null || cs == null || cs.characterClasses == null || cs.characterClasses.Count == 0) return;
                string want = config.CharacterClass;
                if (!string.IsNullOrEmpty(want) && !cs.characterClasses.Any(c => c.Code == want))
                {
                    sapi.Logger.Warning("[testautochar] unknown CharacterClass '" + want + "', using default");
                    want = null;
                }
                string cur = ent.WatchedAttributes.GetString("characterClass");
                if (string.IsNullOrEmpty(want))
                {
                    if (!string.IsNullOrEmpty(cur)) return;
                    want = cs.characterClasses[0].Code;
                }
                else if (cur == want) return;
                cs.setCharacterClass(ent, want, true);
                sapi.Logger.Notification("[testautochar] characterClass set to " + want + " for " + byPlayer.PlayerName);
            }
            catch (Exception e) { sapi?.Logger.Warning("[testautochar] postfix failed: " + e.Message); }
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }
    }
}
