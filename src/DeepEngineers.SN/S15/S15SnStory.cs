using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DeepEngineers.Wire;
using HarmonyLib;
using Story;
using StoryGoalType = Story.GoalType;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S15, Subnautica's story off (formats story.off, aurora.off
 /// play while Subnautica's story is not played in DeepEngineers). Evidence from the decomp (VERIFIED):
 /// <list type="bullet">
 /// <item>Every story line goes through <c>Story.StoryGoal.Execute(string, Story.GoalType)</c>: the scheduled
 /// goals
 /// and the console. PDA
 /// goals call <c>PDALog.Add</c>, radio goals queue a
 /// pending radio message, databank goals
 /// <c>PDAEncyclopedia.AddAndPlaySound</c>. A Harmony prefix skips Execute: no story line, no
 /// radio message, no goal completion (Subnautica's story is not played here).</item>
 /// <item>The Aurora: <c>AuroraWarnings.Update</c> triggers its four warnings between
 /// <c>CrashedShipExploder.timeToStartWarning</c> and <c>timeToStartCountdown</c>
 /// and <c>CrashedShipExploder.Update</c> then starts the countdown voice
 /// (<c>shipCountdownSound</c>), the explosion sound, VFX on the player, <c>MainCameraControl.ShakeCamera</c>
 /// (our picture's camera), <c>DamageSystem.RadiusDamage</c> 2000 at 500 m and swaps the Aurora's model; after
 /// it, every 15-25 s in its biome, a camera shake and a rumble. Decision: held off. A prefix of that Update
 /// moves both times to Story.AuroraNeverS before each Update (so neither the warnings nor the countdown ever trigger);
 /// a save whose Aurora already exploded keeps its model and its Update is skipped (no shakes).</item>
 /// <item>Every PDA voice request is logged
 /// row s15.pda; the oracle checks no story goal's line among them.</item>
 /// </list>
 /// S15.M20 lets both run. Main thread (Harmony prefixes on Unity's Update path). Never throws into the game.
 /// </summary>
    internal static class S15SnStory
    {
        public static bool On = true;
        public static long Skipped, PdaPlays, AuroraHolds, AuroraSkips;
        private static bool _patched, _patchTried;
        private static float _nextAuroraRowS;
        private static string _auroraState;
        private static readonly HashSet<string> SkippedKeys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Once (S15Sn.LoadConfig): the three prefixes.</summary>
        public static void Patch()
        {
            if (_patchTried) return;
            _patchTried = true;
            try
            {
                string v = null;
                try { v = Environment.GetEnvironmentVariable(S15Sn.EnvVar); } catch (Exception) { }
                if (v != null && v.Trim().ToLowerInvariant().Contains("nostory")) On = false;
                var h = new Harmony("deepengineers.sn.s15");
                MethodInfo exec = AccessTools.Method(typeof(StoryGoal), "Execute", new[] { typeof(string), typeof(StoryGoalType) });
                MethodInfo play = AccessTools.Method(typeof(SoundQueue), "Play", new[] { typeof(string), typeof(string) });
                MethodInfo upd = AccessTools.Method(typeof(CrashedShipExploder), "Update", Type.EmptyTypes);
                int n = 0;
                if (exec != null) { h.Patch(exec, prefix: new HarmonyMethod(typeof(S15SnStory).GetMethod(nameof(ExecutePrefix), BindingFlags.Static | BindingFlags.NonPublic))); n++; }
                if (play != null) { h.Patch(play, prefix: new HarmonyMethod(typeof(S15SnStory).GetMethod(nameof(PlayPrefix), BindingFlags.Static | BindingFlags.NonPublic))); n++; }
                if (upd != null) { h.Patch(upd, prefix: new HarmonyMethod(typeof(S15SnStory).GetMethod(nameof(AuroraPrefix), BindingFlags.Static | BindingFlags.NonPublic))); n++; }
                _patched = n == 3;
                DeLog.Info("DE.S15 story " + (On ? "off" : "ON (" + S15Sn.EnvVar + " nostory)") + ": " + n + "/3 patches (StoryGoal.Execute " + (exec != null) + ", SoundQueue.Play " + (play != null) +
                           ", CrashedShipExploder.Update " + (upd != null) + ")" + (_patched ? "" : " MISSING: Subnautica's story lines may play"));
            }
            catch (Exception e)
            {
                DeLog.Warn("DE.Error S15 story patches " + e.GetType().Name + ": " + e.Message + " (Subnautica's story lines may play)");
            }
        }

        private static bool Hold { get { return On && !DevMutants.IsOn(S15SnMutants.StoryOn); } }

        // format story.off: every story goal skipped
        private static bool ExecutePrefix(string key, StoryGoalType goalType)
        {
            try
            {
                bool skip = Hold;
                if (skip) { Skipped++; SkippedKeys.Add(key ?? ""); }
                StoryRow(key, goalType, skip, "game");
                return !skip;
            }
            catch (Exception) { return true; }
        }

        // every PDA voice line that starts: the oracle's evidence
        private static void PlayPrefix(string sound, string subtitles)
        {
            try
            {
                PdaPlays++;
                JsonOut j = S5Rows.Begin("s15.pda");
                j.Str("sound", sound).Str("subtitles", subtitles).Bool("storyKey", subtitles != null && SkippedKeys.Contains(subtitles)).Bool("mutant", DevMutants.IsOn(S15SnMutants.StoryOn));
                S5Rows.End(j);
                DeLog.Info("DE.S15 PDA line " + (sound ?? "?") + " (" + (subtitles ?? "no subtitles") + ")");
            }
            catch (Exception) { }
        }

        // format aurora.off: the countdown and the explosion never start; an exploded Aurora's periodic shakes skipped
        private static bool AuroraPrefix(CrashedShipExploder __instance)
        {
            try
            {
                if (__instance == null || !Hold) { AuroraRow(__instance, "running"); return true; }
                if (__instance.IsExploded()) { AuroraSkips++; AuroraRow(__instance, "exploded: Update skipped"); return false; }
                float never = S15Conventions.Story.AuroraNeverS;
                if (__instance.timeToStartCountdown != never || __instance.timeToStartWarning != never)
                {
                    AuroraHolds++;
                    AuroraRow(__instance, "held (countdown was " + __instance.timeToStartCountdown.ToString("0", CultureInfo.InvariantCulture) + " s)", true);
                    __instance.timeToStartCountdown = never;
                    __instance.timeToStartWarning = never;
                }
                else AuroraRow(__instance, "held");
                return true;
            }
            catch (Exception) { return true; }
        }

        private static void StoryRow(string key, StoryGoalType type, bool skipped, string via)
        {
            string path = null;
            PDALog.EntryData data;
            try { if (type == StoryGoalType.PDA || type == StoryGoalType.Radio) path = PDALog.GetEntryData(key, out data) && data != null && data.sound != null ? data.sound.path : null; }
            catch (Exception) { }
            JsonOut j = S5Rows.Begin("s15.story");
            j.Str("key", key).Str("type", type.ToString()).Bool("skipped", skipped).Str("path", path).Str("via", via).Bool("mutant", DevMutants.IsOn(S15SnMutants.StoryOn));
            S5Rows.End(j);
            DeLog.Info("DE.S15 story goal " + key + " (" + type + ") " + (skipped ? "skipped" : "RUN") + (path != null ? " line " + path : "") + " (" + via + ")");
        }

        private static void AuroraRow(CrashedShipExploder c, string state, bool force = false)
        {
            float now = Time.unscaledTime;
            if (!force && state == _auroraState && now < _nextAuroraRowS) return;
            _auroraState = state;
            _nextAuroraRowS = now + 30f;
            bool countdown = false, explode = false;
            try
            {
                countdown = c != null && c.shipCountdownSound != null && c.shipCountdownSound.GetIsStartingOrPlaying();
                explode = c != null && c.shipExplodeSound != null && c.shipExplodeSound.GetIsStartingOrPlaying();
            }
            catch (Exception) { }
            JsonOut j = S5Rows.Begin("s15.aurora");
            j.Str("state", state).Num("countdownS", c != null ? (double)c.timeToStartCountdown : double.NaN).Num("warningS", c != null ? (double)c.timeToStartWarning : double.NaN)
             .Bool("exploded", c != null && c.IsExploded()).Bool("countdownPlaying", countdown).Bool("explodePlaying", explode)
             .Num("timePassed", DayNightCycle.main != null ? DayNightCycle.main.timePassed : double.NaN).Bool("mutant", DevMutants.IsOn(S15SnMutants.StoryOn));
            S5Rows.End(j);
        }

        /// <summary>
 /// de.s15 {story: "aurora1".."aurora4" | key}: one story goal executed the game's way (StoryGoal.Execute): the Aurora's
 /// warning goal of AuroraWarnings, or a PDA key; the prefix skips it.
 /// </summary>
        public static string Trigger(string what, out string key)
        {
            key = what;
            StoryGoalType type = StoryGoalType.PDA;
            if (what != null && what.StartsWith("aurora", StringComparison.OrdinalIgnoreCase))
            {
                AuroraWarnings aw = UnityEngine.Object.FindObjectOfType<AuroraWarnings>();
                if (aw == null) return "no AuroraWarnings in the scene";
                StoryGoal g = what.EndsWith("4") ? aw.auroraWarning4 : what.EndsWith("3") ? aw.auroraWarning3 : what.EndsWith("2") ? aw.auroraWarning2 : aw.auroraWarning1;
                if (g == null || string.IsNullOrEmpty(g.key)) return "AuroraWarnings has no " + what + " goal";
                key = g.key;
                type = g.goalType;
            }
            if (string.IsNullOrEmpty(key)) return "story: a key or aurora1..aurora4";
            DeLog.Info("DE.S15 story trigger " + key + " (" + type + ", de.s15)");
            StoryGoal.Execute(key, type);
            return null;
        }

        public static string Status()
        {
            return "story " + (On ? "off" : "on") + (_patched ? "" : " (patches missing)") + ": skipped " + Skipped + ", PDA lines " + PdaPlays + ", aurora held " + AuroraHolds + " skipped " + AuroraSkips;
        }
    }
}
