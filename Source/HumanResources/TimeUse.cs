using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SloppyHR
{
    // "Time use" main tab. Samples what every colonist is doing once every 60 ticks, keeps the last three
    // in-game days in hourly slices, and shows each colonist's day as a stacked bar. Meant to move into a standalone mod.

    public class PawnTimeUse : IExposable
    {
        public Pawn pawn;
        public Dictionary<int, Dictionary<string, int>> hours = new Dictionary<int, Dictionary<string, int>>();
        List<string> packed;

        public void Add(int hour, string key, int ticks)
        {
            if (!hours.TryGetValue(hour, out var slice)) hours[hour] = slice = new Dictionary<string, int>();
            slice.TryGetValue(key, out int cur);
            slice[key] = cur + ticks;
        }

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                packed = new List<string>();
                foreach (var h in hours)
                    foreach (var kv in h.Value)
                        packed.Add(h.Key + ";" + kv.Key + ";" + kv.Value);
            }
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Collections.Look(ref packed, "d", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                hours = new Dictionary<int, Dictionary<string, int>>();
                if (packed != null)
                    foreach (string s in packed)
                    {
                        string[] p = s.Split(';');
                        if (p.Length == 3 && int.TryParse(p[0], out int h) && int.TryParse(p[2], out int t)) Add(h, p[1], t);
                    }
            }
            if (Scribe.mode != LoadSaveMode.Saving && Scribe.mode != LoadSaveMode.LoadingVars) packed = null;
        }
    }

    public class TimeUseTracker : GameComponent
    {
        public const int SampleInterval = 60;
        public const int HoursKept = 72;
        public const string WalkPrefix = "w:";

        List<PawnTimeUse> pawns = new List<PawnTimeUse>();
        Dictionary<int, PawnTimeUse> byId = new Dictionary<int, PawnTimeUse>();

        public Dictionary<int, Dictionary<string, int>> backlog = new Dictionary<int, Dictionary<string, int>>();
        List<string> backlogPacked;

        // HR automation (all off by default)
        public bool autoAssign, restUnused;
        public int lastReviewDay = -1;
        public HashSet<string> kept = new HashSet<string>();                          // "pawnId|workType" cells the player wants left alone
        public Dictionary<string, Dictionary<int, int>> resting = new Dictionary<string, Dictionary<int, int>>();   // work type -> pawn id -> priority before it was rested
        List<string> keptList, restingPacked;

        public bool IsResting(string workType) => resting.ContainsKey(workType);

        // Job titles: player-made templates of work priorities, and which colonist holds which.
        public List<JobTitle> titles = new List<JobTitle>();
        // A colonist can hold up to MaxTitles titles, in order: the first is their main job.
        public const int MaxTitles = 5;
        public bool reappliedThisLoad;       // not saved
        public Dictionary<int, List<int>> titleOf = new Dictionary<int, List<int>>();     // pawn id -> title ids, in order
        public int nextTitleId = 1;
        List<string> titleOfPacked;
        readonly Dictionary<int, JobTitle> mergedCache = new Dictionary<int, JobTitle>();

        // Titles in alphabetical order, for menus.
        public List<JobTitle> TitlesSorted => titles.OrderBy(x => x.name ?? "", StringComparer.OrdinalIgnoreCase).ToList();

        JobTitle ById(int id)
        {
            for (int i = 0; i < titles.Count; i++) if (titles[i].id == id) return titles[i];
            return null;
        }

        // The titles a colonist holds, in order.
        public List<JobTitle> TitlesFor(Pawn p)
        {
            var list = new List<JobTitle>();
            if (p == null || !titleOf.TryGetValue(p.thingIDNumber, out List<int> ids)) return list;
            foreach (int id in ids) { JobTitle jt = ById(id); if (jt != null) list.Add(jt); }
            return list;
        }

        public bool HasTitle(Pawn p, JobTitle title) => p != null && title != null && titleOf.TryGetValue(p.thingIDNumber, out List<int> ids) && ids.Contains(title.id);
        public int TitleCount(Pawn p) => TitlesFor(p).Count;

        // What a colonist's titles add up to: the title itself when they hold one, otherwise a merged title
        // named "A + B". Null when they hold none.
        // Merge rule: every title's numbers are used exactly as written. Only when two titles set the same kind of
        // work is there a conflict, and then the title higher in the colonist's list wins.
        public JobTitle TitleFor(Pawn p)
        {
            if (p == null || !titleOf.ContainsKey(p.thingIDNumber)) return null;
            if (mergedCache.TryGetValue(p.thingIDNumber, out JobTitle cached)) return cached;
            List<JobTitle> list = TitlesFor(p);
            JobTitle result = null;
            if (list.Count == 1) result = list[0];
            else if (list.Count > 1)
            {
                result = new JobTitle { id = -1, name = string.Join(" + ", list.Select(x => x.name)) };
                for (int i = 0; i < list.Count; i++)
                    foreach (var kv in list[i].prio)
                        if (kv.Value > 0 && !result.prio.ContainsKey(kv.Key)) result.prio[kv.Key] = kv.Value;
            }
            mergedCache[p.thingIDNumber] = result;
            return result;
        }

        // Boxes the player set by hand on a title holder: pawn id -> work type -> priority (0 = off).
        // They are kept on top of the merged titles whenever the titles are re-applied.
        public Dictionary<int, Dictionary<string, int>> overrides = new Dictionary<int, Dictionary<string, int>>();
        List<string> overridesPacked;

        public Dictionary<string, int> OverridesFor(Pawn p) => p != null && overrides.TryGetValue(p.thingIDNumber, out var d) ? d : null;
        public int OverrideCount(Pawn p) => OverridesFor(p)?.Count ?? 0;

        public void SetOverride(Pawn p, string workType, int priority)
        {
            if (!overrides.TryGetValue(p.thingIDNumber, out var d)) overrides[p.thingIDNumber] = d = new Dictionary<string, int>();
            d[workType] = priority;
        }

        public void ClearOverride(Pawn p, string workType)
        {
            if (p == null || !overrides.TryGetValue(p.thingIDNumber, out var d)) return;
            d.Remove(workType);
            if (d.Count == 0) overrides.Remove(p.thingIDNumber);
        }

        public void ClearOverrides(Pawn p) { if (p != null) overrides.Remove(p.thingIDNumber); }

        // Call after anything about titles changes (assignments, a title's priorities or its name).
        public void TitlesChanged() => mergedCache.Clear();

        public bool AddTitle(Pawn p, JobTitle title)
        {
            if (p == null || title == null) return false;
            if (!titleOf.TryGetValue(p.thingIDNumber, out List<int> ids)) titleOf[p.thingIDNumber] = ids = new List<int>();
            ids.RemoveAll(id => ById(id) == null);
            if (ids.Contains(title.id)) return true;
            if (ids.Count >= MaxTitles) return false;
            ids.Add(title.id);
            TitlesChanged();
            return true;
        }

        public void SetOnlyTitle(Pawn p, JobTitle title)
        {
            if (p == null || title == null) return;
            titleOf[p.thingIDNumber] = new List<int> { title.id };
            TitlesChanged();
        }

        public void RemoveTitle(Pawn p, JobTitle title)
        {
            if (p == null || title == null || !titleOf.TryGetValue(p.thingIDNumber, out List<int> ids)) return;
            ids.Remove(title.id);
            if (ids.Count == 0) { titleOf.Remove(p.thingIDNumber); overrides.Remove(p.thingIDNumber); }
            TitlesChanged();
        }

        public void ClearTitles(Pawn p)
        {
            if (p != null) { titleOf.Remove(p.thingIDNumber); overrides.Remove(p.thingIDNumber); }
            TitlesChanged();
        }

        public void MakeFirst(Pawn p, JobTitle title)
        {
            if (p == null || title == null || !titleOf.TryGetValue(p.thingIDNumber, out List<int> ids) || !ids.Remove(title.id)) return;
            ids.Insert(0, title.id);
            TitlesChanged();
        }

        // A title was deleted: take it off everyone.
        public void ForgetTitle(JobTitle title)
        {
            foreach (int pid in titleOf.Keys.ToList())
            {
                titleOf[pid].Remove(title.id);
                if (titleOf[pid].Count == 0) { titleOf.Remove(pid); overrides.Remove(pid); }
            }
            TitlesChanged();
        }

        // Adds up a colonist's tracked time by category over the last hours (walking detail excluded).
        public void Visit(Pawn p, int hoursBack, Action<string, int> visit)
        {
            if (p == null || !byId.TryGetValue(p.thingIDNumber, out PawnTimeUse rec)) return;
            int first = Find.TickManager.TicksGame / GenDate.TicksPerHour - hoursBack + 1;
            foreach (var h in rec.hours)
            {
                if (h.Key < first) continue;
                foreach (var kv in h.Value) if (!kv.Key.StartsWith(WalkPrefix)) visit(kv.Key, kv.Value);
            }
        }

        public TimeUseTracker(Game game) { }

        public void SampleBacklog()
        {
            try
            {
                int hour = Find.TickManager.TicksGame / GenDate.TicksPerHour;
                backlog[hour] = Workload.Count();
                List<int> old = backlog.Keys.Where(h => h <= hour - HoursKept).ToList();
                foreach (int h in old) backlog.Remove(h);
            }
            catch (Exception e) { Log.ErrorOnce(HR.Tag + "Workload sampling failed: " + e, 81730062); }
        }

        public int ColonyTicks(string key, int hoursBack)
        {
            int first = Find.TickManager.TicksGame / GenDate.TicksPerHour - hoursBack + 1, sum = 0;
            foreach (PawnTimeUse rec in pawns)
                foreach (var h in rec.hours)
                    if (h.Key >= first && h.Value.TryGetValue(key, out int t)) sum += t;
            return sum;
        }

        public static TimeUseTracker Instance => Current.Game?.GetComponent<TimeUseTracker>();
        public List<PawnTimeUse> Pawns => pawns;

        public override void GameComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % GenDate.TicksPerHour == 0)
            {
                SampleBacklog();
                try { HrAutomation.Hourly(this); }
                catch (Exception e) { Log.ErrorOnce(HR.Tag + "HR automation failed: " + e, 81730064); }
            }
            if (now % SampleInterval != 0) return;
            try
            {
                int hour = now / GenDate.TicksPerHour;
                Faction player = Faction.OfPlayerSilentFail;
                if (player == null) return;
                List<Map> maps = Find.Maps;
                for (int m = 0; m < maps.Count; m++)
                {
                    List<Pawn> list = maps[m].mapPawns.SpawnedPawnsInFaction(player);
                    for (int i = 0; i < list.Count; i++)
                    {
                        Pawn p = list[i];
                        if (p == null || p.Dead || !p.RaceProps.Humanlike) continue;
                        if (!byId.TryGetValue(p.thingIDNumber, out PawnTimeUse rec))
                        {
                            rec = new PawnTimeUse { pawn = p };
                            pawns.Add(rec);
                            byId[p.thingIDNumber] = rec;
                        }
                        string key = Classify(p);
                        rec.Add(hour, key, SampleInterval);
                        if (key != "sleep" && p.pather != null && p.pather.MovingNow) rec.Add(hour, WalkPrefix + key, SampleInterval);
                    }
                }
                if (now % GenDate.TicksPerHour == 0) Prune(hour);
            }
            catch (Exception e) { Log.ErrorOnce(HR.Tag + "Time use sampling failed: " + e, 81730061); }
        }

        void Prune(int hour)
        {
            int oldest = hour - HoursKept + 1;
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                PawnTimeUse rec = pawns[i];
                Pawn p = rec.pawn;
                if (p != null)
                {
                    List<int> old = null;
                    foreach (int h in rec.hours.Keys) if (h < oldest) (old ?? (old = new List<int>())).Add(h);
                    if (old != null) foreach (int h in old) rec.hours.Remove(h);
                }
                if (p == null || p.Dead || p.Destroyed && !p.IsWorldPawn() || p.Discarded || p.Faction != Faction.OfPlayerSilentFail || rec.hours.Count == 0)
                {
                    if (p != null) byId.Remove(p.thingIDNumber);
                    pawns.RemoveAt(i);
                }
            }
        }

        public float Share(Pawn p, string key, int hoursBack)
        {
            if (p == null || !byId.TryGetValue(p.thingIDNumber, out PawnTimeUse rec)) return -1f;
            int first = Find.TickManager.TicksGame / GenDate.TicksPerHour - hoursBack + 1;
            int total = 0, hit = 0;
            foreach (var h in rec.hours)
            {
                if (h.Key < first) continue;
                foreach (var kv in h.Value)
                {
                    if (kv.Key.StartsWith(WalkPrefix)) continue;
                    total += kv.Value;
                    if (kv.Key == key) hit += kv.Value;
                }
            }
            return total < GenDate.TicksPerHour * 3 ? -1f : (float)hit / total;
        }

        public static string Classify(Pawn p)
        {
            if (p.Downed) return "medical";
            if (p.InMentalState) return "mental";
            if (p.Drafted) return "drafted";
            Job j = p.CurJob;
            if (j == null) return "idle";
            JobDriver drv = p.jobs.curDriver;
            if (drv != null && drv.asleep) return "sleep";
            if (j.def == JobDefOf.LayDown || j.def == JobDefOf.LayDownResting || j.def == JobDefOf.LayDownAwake)
            {
                Building_Bed bed = p.CurrentBed();
                return (bed != null && bed.Medical) || HealthAIUtility.ShouldSeekMedicalRest(p) ? "medical" : "sleep";
            }
            if (j.def.joyKind != null) return "joy";
            if (j.def == JobDefOf.Ingest) return "eat";
            if (j.workGiverDef?.workType != null) return "wt:" + j.workGiverDef.workType.defName;
            if (p.mindState?.lastJobTag == JobTag.Idle || j.def == JobDefOf.Wait_Wander || j.def == JobDefOf.GotoWander) return "idle";
            if (p.GetLord() != null) return "group";
            return "o:" + j.def.defName;
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) Prune(Find.TickManager.TicksGame / GenDate.TicksPerHour);
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Deep);
            Scribe_Values.Look(ref JobBadges.enabled, "badges", true);
            Scribe_Values.Look(ref autoAssign, "autoAssign", false);
            Scribe_Values.Look(ref restUnused, "restUnused", false);
            Scribe_Values.Look(ref lastReviewDay, "lastReviewDay", -1);
            Scribe_Values.Look(ref nextTitleId, "nextTitleId", 1);
            Scribe_Collections.Look(ref titles, "titles", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.Saving) titleOfPacked = titleOf.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key + ";" + string.Join(",", kv.Value)).ToList();
            Scribe_Collections.Look(ref titleOfPacked, "titleOf", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.Saving)
                overridesPacked = overrides.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key + ";" + string.Join(",", kv.Value.Select(o => o.Key + "=" + o.Value))).ToList();
            Scribe_Collections.Look(ref overridesPacked, "titleOverrides", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                overrides = new Dictionary<int, Dictionary<string, int>>();
                if (overridesPacked != null)
                    foreach (string line in overridesPacked)
                    {
                        string[] p = line.Split(';');
                        if (p.Length != 2 || !int.TryParse(p[0], out int pid)) continue;
                        var d = new Dictionary<string, int>();
                        foreach (string part in p[1].Split(','))
                        {
                            string[] kv = part.Split('=');
                            if (kv.Length == 2 && int.TryParse(kv[1], out int pr)) d[kv[0]] = pr;
                        }
                        if (d.Count > 0) overrides[pid] = d;
                    }
                overridesPacked = null;
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (titles == null) titles = new List<JobTitle>();
                titles.RemoveAll(x => x == null);
                titleOf = new Dictionary<int, List<int>>();
                mergedCache.Clear();
                if (titleOfPacked != null)
                    foreach (string line in titleOfPacked)
                    {
                        // "pawn;title" from older saves, "pawn;title,title,title" now
                        string[] p = line.Split(';');
                        if (p.Length != 2 || !int.TryParse(p[0], out int pid)) continue;
                        var ids = new List<int>();
                        foreach (string part in p[1].Split(',')) if (int.TryParse(part, out int tid) && !ids.Contains(tid) && ids.Count < MaxTitles) ids.Add(tid);
                        if (ids.Count > 0) titleOf[pid] = ids;
                    }
                titleOfPacked = null;
            }
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                keptList = kept.ToList();
                restingPacked = new List<string>();
                foreach (var w in resting) foreach (var kv in w.Value) restingPacked.Add(w.Key + ";" + kv.Key + ";" + kv.Value);
            }
            Scribe_Collections.Look(ref keptList, "kept", LookMode.Value);
            Scribe_Collections.Look(ref restingPacked, "resting", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                kept = keptList != null ? new HashSet<string>(keptList) : new HashSet<string>();
                resting = new Dictionary<string, Dictionary<int, int>>();
                if (restingPacked != null)
                    foreach (string line in restingPacked)
                    {
                        string[] p = line.Split(';');
                        if (p.Length != 3 || !int.TryParse(p[1], out int id) || !int.TryParse(p[2], out int pr)) continue;
                        if (!resting.TryGetValue(p[0], out var d)) resting[p[0]] = d = new Dictionary<int, int>();
                        d[id] = pr;
                    }
                keptList = null; restingPacked = null;
            }
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                backlogPacked = new List<string>();
                foreach (var h in backlog) foreach (var kv in h.Value) backlogPacked.Add(h.Key + ";" + kv.Key + ";" + kv.Value);
            }
            Scribe_Collections.Look(ref backlogPacked, "backlog", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                backlog = new Dictionary<int, Dictionary<string, int>>();
                if (backlogPacked != null)
                    foreach (string line in backlogPacked)
                    {
                        string[] p = line.Split(';');
                        if (p.Length != 3 || !int.TryParse(p[0], out int h) || !int.TryParse(p[2], out int v)) continue;
                        if (!backlog.TryGetValue(h, out var d)) backlog[h] = d = new Dictionary<string, int>();
                        d[p[1]] = v;
                    }
                backlogPacked = null;
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (pawns == null) pawns = new List<PawnTimeUse>();
                pawns.RemoveAll(r => r == null || r.pawn == null);
                byId = new Dictionary<int, PawnTimeUse>();
                foreach (PawnTimeUse r in pawns) byId[r.pawn.thingIDNumber] = r;
            }
        }
    }

    public class MainTabWindow_TimeUse : MainTabWindow
    {
        class Row
        {
            public Pawn pawn;                 // null = whole colony
            public string name, bestSkill, skillTip;
            public Dictionary<string, int> cats = new Dictionary<string, int>();     // display category -> ticks
            public Dictionary<string, int> walk = new Dictionary<string, int>();     // display category -> walking ticks inside it
            public Dictionary<string, int> other = new Dictionary<string, int>();    // job defName -> ticks (inside "other")
            public int total;
            public float Share(string cat) => total > 0 && cats.TryGetValue(cat, out int t) ? (float)t / total : 0f;
        }

        static readonly int[] RangeHours = { 6, 24, 72 };
        static readonly string[] RangeLabels = { "Last 6 hours", "Last 24 hours", "Last 3 days" };
        static int range = 1;
        static bool splitWalking;
        static string sortCat;
        static readonly HashSet<string> hidden = new HashSet<string>();   // categories taken out of the bars; shares are recalculated without them

        List<Row> rows = new List<Row>();
        List<string> order = new List<string>();    // categories, biggest colony share first
        int cacheStamp = -1;
        Vector2 scroll;

        // The Job titles page lays the priorities out in one row like the Work tab, so it asks for a wider window.
        public override Vector2 RequestedTabSize => new Vector2(
            Mathf.Min(page == 3 ? Mathf.Max(1150f, 36f + JobTitles.WantedWidth) : 1150f, UI.screenWidth), Mathf.Min(660f, UI.screenHeight - 80f));

        public override void PreOpen()
        {
            base.PreOpen();
            cacheStamp = -1;
            HrSelection.Opened();
            // once per loaded game: bring every title holder's priorities in line with the current merge rule
            TimeUseTracker tr = TimeUseTracker.Instance;
            if (tr != null && !tr.reappliedThisLoad)
            {
                tr.reappliedThisLoad = true;
                try { foreach (Pawn p in JobAdvisor.Pawns) if (tr.TitleCount(p) > 1) JobTitles.Reapply(tr, p); }
                catch (Exception e) { Log.Warning(HR.Tag + "could not re-apply merged titles: " + e.Message); }
            }
        }

        // ---- labels and colours ----

        internal static string Label(string cat)
        {
            switch (cat)
            {
                case "sleep": return "Sleep / rest";
                case "eat": return "Eating";
                case "joy": return "Recreation";
                case "idle": return "Idle / wandering";
                case "drafted": return "Drafted";
                case "medical": return "Downed / medical rest";
                case "mental": return "Mental break";
                case "group": return "Group activity";
                case "walk": return "Walking";
                case "other": return "Other";
            }
            if (cat.StartsWith("wt:"))
            {
                WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail(cat.Substring(3));
                string l = wt?.gerundLabel.NullOrEmpty() == false ? wt.gerundLabel : wt?.labelShort;
                return l.NullOrEmpty() ? cat.Substring(3) : l.CapitalizeFirst();
            }
            return cat;
        }

        static readonly Dictionary<string, Color> colors = new Dictionary<string, Color>();

        static Color Hsv(float h, float s, float v)
        {
            h = (h % 1f + 1f) % 1f * 6f;
            int i = (int)h;
            float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
            switch (i % 6)
            {
                case 0: return new Color(v, t, p);
                case 1: return new Color(q, v, p);
                case 2: return new Color(p, v, t);
                case 3: return new Color(p, q, v);
                case 4: return new Color(t, p, v);
                default: return new Color(v, p, q);
            }
        }

        internal static Color ColorOf(string cat)
        {
            if (colors.TryGetValue(cat, out Color c)) return c;
            switch (cat)
            {
                case "sleep": c = new Color(0.30f, 0.36f, 0.70f); break;
                case "eat": c = new Color(0.85f, 0.62f, 0.25f); break;
                case "joy": c = new Color(0.88f, 0.45f, 0.72f); break;
                case "idle": c = new Color(0.55f, 0.55f, 0.55f); break;
                case "drafted": c = new Color(0.78f, 0.22f, 0.22f); break;
                case "medical": c = new Color(0.92f, 0.80f, 0.80f); break;
                case "mental": c = new Color(0.55f, 0.20f, 0.60f); break;
                case "group": c = new Color(0.30f, 0.72f, 0.72f); break;
                case "walk": c = new Color(0.92f, 0.90f, 0.50f); break;
                case "other": c = new Color(0.38f, 0.33f, 0.28f); break;
                default:
                    int idx = 0;
                    if (cat.StartsWith("wt:"))
                    {
                        WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail(cat.Substring(3));
                        idx = wt != null ? DefDatabase<WorkTypeDef>.AllDefsListForReading.IndexOf(wt) : Math.Abs(cat.GetHashCode() % 97);
                    }
                    c = Hsv(0.08f + idx * 0.618034f, 0.50f + 0.20f * (idx % 3) / 2f, 0.62f + 0.24f * (idx % 2));
                    break;
            }
            colors[cat] = c;
            return c;
        }

        // ---- data ----

        void Rebuild()
        {
            rows.Clear();
            order.Clear();
            TimeUseTracker tracker = TimeUseTracker.Instance;
            if (tracker == null) return;
            int hourNow = Find.TickManager.TicksGame / GenDate.TicksPerHour;
            int first = hourNow - RangeHours[range] + 1;
            Row colony = new Row { name = "Whole colony" };
            foreach (PawnTimeUse rec in tracker.Pawns)
            {
                if (rec.pawn == null) continue;
                Row row = new Row { pawn = rec.pawn, name = rec.pawn.LabelShortCap };
                if (rec.pawn.skills != null)
                {
                    List<SkillRecord> top = rec.pawn.skills.skills.Where(sk => !sk.TotallyDisabled).OrderByDescending(sk => sk.Level).ThenByDescending(sk => (int)sk.passion).Take(3).ToList();
                    if (top.Count > 0)
                    {
                        row.bestSkill = top[0].def.skillLabel.CapitalizeFirst() + " " + top[0].Level;
                        row.skillTip = "Highest skills:\n" + string.Join("\n", top.Select(sk => sk.def.skillLabel.CapitalizeFirst() + " " + sk.Level));
                    }
                }
                foreach (var h in rec.hours)
                {
                    if (h.Key < first) continue;
                    foreach (var kv in h.Value)
                    {
                        bool isWalk = kv.Key.StartsWith(TimeUseTracker.WalkPrefix);
                        string raw = isWalk ? kv.Key.Substring(TimeUseTracker.WalkPrefix.Length) : kv.Key;
                        string cat = raw.StartsWith("o:") ? "other" : raw;
                        if (isWalk) Bump(row.walk, cat, kv.Value);
                        else
                        {
                            Bump(row.cats, cat, kv.Value);
                            row.total += kv.Value;
                            if (cat == "other") Bump(row.other, raw.Substring(2), kv.Value);
                        }
                    }
                }
                if (row.total == 0) continue;
                if (splitWalking)
                {
                    int walked = 0;
                    foreach (var w in row.walk)
                    {
                        if (!row.cats.TryGetValue(w.Key, out int t)) continue;
                        int take = Math.Min(t, w.Value);
                        row.cats[w.Key] = t - take;
                        walked += take;
                    }
                    if (walked > 0) row.cats["walk"] = walked;
                }
                foreach (string h in hidden)
                    if (row.cats.TryGetValue(h, out int ht)) { row.total -= ht; row.cats.Remove(h); }
                if (row.total <= 0) continue;
                rows.Add(row);
                foreach (var kv in row.cats) Bump(colony.cats, kv.Key, kv.Value);
                foreach (var kv in row.walk) Bump(colony.walk, kv.Key, kv.Value);
                foreach (var kv in row.other) Bump(colony.other, kv.Key, kv.Value);
                colony.total += row.total;
            }
            order.AddRange(colony.cats.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => kv.Key));
            if (sortCat != null && !order.Contains(sortCat)) sortCat = null;
            if (sortCat == null) rows.SortBy(r => r.name);
            else rows.SortByDescending(r => r.Share(sortCat));
            if (colony.total > 0) rows.Insert(0, colony);
        }

        static void Bump(Dictionary<string, int> d, string k, int v)
        {
            d.TryGetValue(k, out int cur);
            d[k] = cur + v;
        }

        static string Hours(int ticks) => ((float)ticks / GenDate.TicksPerHour).ToString("0.0") + "h";

        // ---- drawing ----

        static int page;

        public override void DoWindowContents(Rect inRect)
        {
            HrSelection.Sync();
            Text.Font = GameFont.Small;
            string[] pages = { "Time use", "Job advisor", "Workload", "Job titles" };
            for (int i = 0; i < pages.Length; i++)
            {
                Rect b = new Rect(inRect.x + i * 146f, inRect.y, 140f, 30f);
                if (page == i) Widgets.DrawHighlightSelected(b);
                if (Widgets.ButtonText(b, pages[i]) && page != i) { page = i; SetInitialSizeAndPosition(); }
            }
            Widgets.DrawLineHorizontal(inRect.x, inRect.y + 34f, inRect.width);
            Rect body = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
            if (page == 1) JobAdvisor.Draw(body);
            else if (page == 2) Workload.Draw(body);
            else if (page == 3) JobTitles.Draw(body);
            else DrawTimeUse(body);
        }

        void DrawTimeUse(Rect inRect)
        {
            int stamp = Find.TickManager.TicksGame / 120 * 8 + range * 2 + (splitWalking ? 1 : 0);
            if (stamp != cacheStamp) { Rebuild(); cacheStamp = stamp; }

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            float x = inRect.x;
            for (int i = 0; i < RangeLabels.Length; i++)
            {
                Rect b = new Rect(x, inRect.y, 130f, 28f);
                if (range == i) Widgets.DrawHighlightSelected(b);
                if (Widgets.ButtonText(b, RangeLabels[i])) { range = i; cacheStamp = -1; }
                x += 136f;
            }
            Rect walkRect = new Rect(x + 10f, inRect.y, 190f, 28f);
            bool before = splitWalking;
            Widgets.CheckboxLabeled(walkRect, "Show walking separately", ref splitWalking);
            if (before != splitWalking) cacheStamp = -1;
            TooltipHandler.TipRegion(walkRect, "Off: time spent walking counts toward the task being walked to.\nOn: all walking is pulled out of the tasks and shown as its own slice.");
            Rect badgeRect = new Rect(walkRect.xMax + 16f, inRect.y, 170f, 28f);
            Widgets.CheckboxLabeled(badgeRect, "Badges on portraits", ref JobBadges.enabled);
            TooltipHandler.TipRegion(badgeRect, "Optional. Shows a small coloured badge on each colonist's portrait in the colonist bar for what they are doing right now. Colours match this window. This choice is saved with the game.");
            walkRect = badgeRect;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(walkRect.xMax + 16f, inRect.y, inRect.xMax - walkRect.xMax - 16f, 28f),
                (sortCat == null ? "Sorted by name." : "Ranked by " + Label(sortCat) + ".") + " Legend: click hides a type, right-click ranks by it.");
            GUI.color = Color.white;

            float y = inRect.y + 36f;
            if (rows.Count == 0 && hidden.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 60f), "No data yet. Unpause and let the colony run for a bit.");
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            // legend
            Text.Font = GameFont.Tiny;
            float lx = inRect.x;
            Row colony = rows.Count > 0 && rows[0].pawn == null ? rows[0] : null;
            foreach (string cat in order.Concat(hidden.OrderBy(Label)).ToList())
            {
                bool isHidden = hidden.Contains(cat);
                string text = Label(cat) + (isHidden ? " (hidden)" : colony != null ? " " + colony.Share(cat).ToStringPercent("0") : "");
                float w = Text.CalcSize(text).x + 26f;
                if (lx + w > inRect.xMax) { lx = inRect.x; y += 20f; }
                Rect r = new Rect(lx, y, w, 18f);
                if (sortCat == cat) Widgets.DrawHighlightSelected(r);
                else Widgets.DrawHighlightIfMouseover(r);
                Color sw = ColorOf(cat);
                Widgets.DrawBoxSolid(new Rect(lx + 3f, y + 3f, 12f, 12f), isHidden ? new Color(sw.r, sw.g, sw.b, 0.25f) : sw);
                GUI.color = isHidden ? new Color(1f, 1f, 1f, 0.4f) : Color.white;
                Widgets.Label(new Rect(lx + 20f, y, w - 20f, 18f), text);
                GUI.color = Color.white;
                TooltipHandler.TipRegion(r, isHidden ? "Hidden. Click to show it again." : "Click to hide this type; the bars and percentages are recalculated from what remains.\nRight-click to rank colonists by it.");
                if (Mouse.IsOver(r) && Event.current.type == EventType.MouseDown && Event.current.button == 1)
                {
                    if (!isHidden) { sortCat = sortCat == cat ? null : cat; cacheStamp = -1; }
                    Event.current.Use();
                }
                else if (Widgets.ButtonInvisible(r))
                {
                    if (!hidden.Remove(cat)) { hidden.Add(cat); if (sortCat == cat) sortCat = null; }
                    cacheStamp = -1;
                }
                lx += w + 6f;
            }
            if (hidden.Count > 0)
            {
                string all = "Show all";
                float aw = Text.CalcSize(all).x + 16f;
                if (lx + aw > inRect.xMax) { lx = inRect.x; y += 20f; }
                if (Widgets.ButtonText(new Rect(lx, y - 1f, aw, 20f), all)) { hidden.Clear(); cacheStamp = -1; }
            }
            y += 26f;

            // table
            const float rowH = 26f, nameOnlyW = 140f, skillW = 120f, nameW = nameOnlyW + skillW, hoursW = 56f;
            Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, rows.Count * rowH);
            if (HrSelection.TakeScroll(0))
            {
                int at = rows.FindIndex(r => r.pawn != null && r.pawn.thingIDNumber == HrSelection.PawnId);
                if (at >= 0) scroll.y = Mathf.Clamp(at * rowH - outRect.height / 2f + rowH, 0f, Mathf.Max(0f, viewRect.height - outRect.height));
            }
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float barW = viewRect.width - nameW - hoursW - 8f;
            for (int i = 0; i < rows.Count; i++)
            {
                float ry = i * rowH;
                if (ry + rowH < scroll.y || ry > scroll.y + outRect.height) continue;
                Row row = rows[i];
                Rect full = new Rect(0f, ry, viewRect.width, rowH);
                if (i % 2 == 1) Widgets.DrawLightHighlight(full);
                if (row.pawn != null && row.pawn.thingIDNumber == HrSelection.PawnId) Widgets.DrawHighlightSelected(full);

                Rect nameRect = new Rect(4f, ry, nameOnlyW - 4f, rowH);
                if (row.bestSkill != null)
                {
                    Rect skillRect = new Rect(nameOnlyW, ry, skillW - 4f, rowH);
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Text.WordWrap = false;
                    GUI.color = new Color(0.75f, 0.85f, 1f);
                    Widgets.Label(skillRect, row.bestSkill);
                    GUI.color = Color.white;
                    Text.WordWrap = true;
                    TooltipHandler.TipRegion(skillRect, row.skillTip);
                }
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleLeft;
                Text.WordWrap = false;
                Widgets.Label(nameRect, row.name);
                Text.WordWrap = true;
                if (row.pawn != null)
                {
                    Widgets.DrawHighlightIfMouseover(nameRect);
                    if (Widgets.ButtonInvisible(nameRect) && (row.pawn.Spawned || row.pawn.IsWorldPawn())) CameraJumper.TryJumpAndSelect(row.pawn);
                }

                float bx = nameW;
                Rect bar = new Rect(bx, ry + 3f, barW, rowH - 6f);
                Widgets.DrawBoxSolid(bar, new Color(0.12f, 0.12f, 0.12f));
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                foreach (string cat in order)
                {
                    if (!row.cats.TryGetValue(cat, out int t) || t <= 0) continue;
                    float share = (float)t / row.total;
                    float w = share * barW;
                    Rect seg = new Rect(bx, bar.y, w, bar.height);
                    Widgets.DrawBoxSolid(seg, ColorOf(cat));
                    if (w >= 30f)
                    {
                        GUI.color = new Color(0f, 0f, 0f, 0.85f);
                        Widgets.Label(seg, share.ToStringPercent("0"));
                        GUI.color = Color.white;
                    }
                    if (Mouse.IsOver(seg))
                    {
                        Widgets.DrawBox(seg);
                        string tip = row.name + "\n" + Label(cat) + ": " + Hours(t) + " (" + share.ToStringPercent("0.#") + ")";
                        if (!splitWalking && cat != "walk" && row.walk.TryGetValue(cat, out int wk) && wk > 0)
                            tip += "\nof which walking: " + Hours(Math.Min(wk, t)) + " (" + ((float)Math.Min(wk, t) / t).ToStringPercent("0") + " of it)";
                        if (cat == "other" && row.other.Count > 0)
                            tip += "\n\n" + string.Join("\n", row.other.OrderByDescending(o => o.Value).Take(8)
                                .Select(o => "- " + (DefDatabase<JobDef>.GetNamedSilentFail(o.Key)?.defName ?? o.Key) + ": " + Hours(o.Value)));
                        TooltipHandler.TipRegion(seg, tip);
                    }
                    bx += w;
                }
                GUI.color = Color.gray;
                Text.Anchor = TextAnchor.MiddleRight;
                Rect hoursRect = new Rect(viewRect.width - hoursW, ry, hoursW - 4f, rowH);
                Widgets.Label(hoursRect, Hours(row.total));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(hoursRect, row.pawn == null ? "Colonist-hours tracked in this period." : "Hours tracked in this period. Time away from a map (caravans, before joining) isn't tracked.");
            }
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }

    // The colonist the HR pages are focused on. It follows the colonist selected in the game, so whoever you have
    // selected when you open HR (or click while it is open) is the one highlighted on every page.
    public static class HrSelection
    {
        public static int PawnId = -1;
        static Thing lastGameSelection;
        static readonly bool[] scrollPending = new bool[3];     // 0 time use, 1 job advisor, 2 colonists list

        public static void Opened() { lastGameSelection = null; }

        public static void Sync()
        {
            Thing sel = Find.Selector?.SingleSelectedThing;
            if (sel == lastGameSelection) return;
            lastGameSelection = sel;
            if (sel is Pawn p && p.IsColonist) Set(p.thingIDNumber);
        }

        public static void Set(int id)
        {
            if (id == PawnId) return;
            PawnId = id;
            for (int i = 0; i < scrollPending.Length; i++) scrollPending[i] = true;
        }

        // Picking a colonist inside HR also selects them in the game (without moving the camera), so the Work tab
        // and the rest of the interface follow.
        public static void SelectInGame(Pawn p)
        {
            try
            {
                if (p == null || !p.Spawned || p.Map != Find.CurrentMap || Find.Selector.IsSelected(p)) return;
                Find.Selector.ClearSelection();
                Find.Selector.Select(p, false, false);
                lastGameSelection = p;
            }
            catch { }
        }

        // True once per selection change for each page, so the page can scroll its list to the colonist.
        public static bool TakeScroll(int page)
        {
            bool was = scrollPending[page];
            scrollPending[page] = false;
            return was && PawnId >= 0;
        }
    }
}
