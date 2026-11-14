using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace SloppyHR
{
    // a small coloured badge on each colonist-bar portrait showing what that
    // colonist is doing right now, using the Time use window's categories and colours.
    [StaticConstructorOnStartup]
    public static class JobBadges
    {
        public static bool enabled = true;

        sealed class Entry { public string cat; public int tick = -9999; }
        static readonly Dictionary<int, Entry> cache = new Dictionary<int, Entry>();
        static readonly Dictionary<string, string> abbrevs = new Dictionary<string, string>();

        static JobBadges()
        {
            try
            {
                MethodInfo draw = AccessTools.Method(typeof(ColonistBarColonistDrawer), nameof(ColonistBarColonistDrawer.DrawColonist));
                if (draw == null) { Log.Warning(HR.Tag + "Job badges not applied: colonist bar draw method not found"); return; }
                new Harmony(HR.HarmonyId).Patch(draw, postfix: new HarmonyMethod(typeof(JobBadges), nameof(Postfix)));
            }
            catch (Exception e) { Log.Warning(HR.Tag + "Job badges not applied: " + HR.Reason(e)); }
        }

        static string Abbrev(string cat)
        {
            if (abbrevs.TryGetValue(cat, out string a)) return a;
            string label = cat.StartsWith("o:") ? "Other" : MainTabWindow_TimeUse.Label(cat);
            a = label.Substring(0, 1).ToUpperInvariant();
            for (int i = 1; i < label.Length; i++)
            {
                char c = char.ToLowerInvariant(label[i]);
                if (!char.IsLetter(c)) break;
                if ("aeiou".IndexOf(c) < 0) { a += c; break; }
            }
            abbrevs[cat] = a;
            return a;
        }

        public static void Postfix(Rect rect, Pawn colonist)
        {
            if (!enabled || colonist == null || colonist.Dead || !colonist.Spawned || Event.current.type != EventType.Repaint && !Mouse.IsOver(rect)) return;
            try
            {
                int now = Find.TickManager.TicksGame;
                if (!cache.TryGetValue(colonist.thingIDNumber, out Entry e)) cache[colonist.thingIDNumber] = e = new Entry();
                if (e.cat == null || now - e.tick >= 30 || now < e.tick)
                {
                    e.cat = TimeUseTracker.Classify(colonist);
                    e.tick = now;
                    if (cache.Count > 2000) cache.Clear();
                }
                string cat = e.cat.StartsWith("o:") ? "other" : e.cat;
                float size = Mathf.Clamp(rect.width * 0.40f, 12f, 20f);
                Rect badge = new Rect(rect.xMax - size + 2f, rect.y - 2f, size, size);
                float alpha = Find.ColonistBar.GetEntryRectAlpha(rect);
                Color col = MainTabWindow_TimeUse.ColorOf(cat);

                Widgets.DrawBoxSolid(badge, new Color(0f, 0f, 0f, alpha));
                Widgets.DrawBoxSolid(badge.ContractedBy(1f), new Color(col.r, col.g, col.b, alpha));
                if (size >= 15f)
                {
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    bool dark = col.r * 0.30f + col.g * 0.59f + col.b * 0.11f > 0.55f;
                    GUI.color = dark ? new Color(0f, 0f, 0f, alpha) : new Color(1f, 1f, 1f, alpha);
                    Widgets.Label(new Rect(badge.x - 4f, badge.y - 1f, badge.width + 8f, badge.height + 2f), Abbrev(e.cat));
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.Font = GameFont.Small;
                }
                if (Mouse.IsOver(badge))
                {
                    string report = colonist.jobs?.curDriver?.GetReport();
                    TooltipHandler.TipRegion(badge, MainTabWindow_TimeUse.Label(cat) + (report.NullOrEmpty() ? "" : "\n" + report.CapitalizeFirst()));
                }
            }
            catch (Exception ex) { Log.ErrorOnce(HR.Tag + "Job badge drawing failed: " + ex, 81730071); }
        }
    }
}
