using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace SloppyHR
{
    // "Workload" page. Once an in-game hour the tracker counts how much work of each type is waiting on the
    // home maps. This page shows which work never gets cleared or keeps growing, and who could be put on it.
    public static class Workload
    {
        class Line
        {
            public WorkTypeDef wt;
            public int now, dayAgo, streak, level;      // level: 0 clear, 1 keeping up, 2 slow, 3 falling behind, 4 needs attention
            public float labourHours;
            public int[] history;                       // last 48 hourly samples, -1 = no sample
            public string status;
            public bool stuck, bills;
        }

        static Dictionary<ThingDef, WorkTypeDef> billTypes;
        static List<Line> lines = new List<Line>();
        static HashSet<string> hot = new HashSet<string>();
        static Dictionary<string, int> levels = new Dictionary<string, int>();
        static HashSet<string> unattended = new HashSet<string>();

        // True when work has waited a day and nobody worked on it: the people assigned never get to it, so the
        // assignments need changing. (Work that is behind despite steady labour needs more workers instead.)
        public static bool Unattended(string workTypeDefName) { Ensure(); return unattended.Contains(workTypeDefName); }

        // 0 clear, 1 keeping up, 2 slow, 3 falling behind, 4 needs attention
        public static int Level(string workTypeDefName) { Ensure(); return levels.TryGetValue(workTypeDefName, out int l) ? l : 0; }
        static int stamp = -1;
        static Vector2 scroll;

        static void Bump(Dictionary<string, int> d, string k, int v) { if (v <= 0) return; d.TryGetValue(k, out int c); d[k] = c + v; }

        static IEnumerable<GlobalTargetInfo> Designated(Map map, string defName)
        {
            DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(defName);
            if (def == null) return Enumerable.Empty<GlobalTargetInfo>();
            return map.designationManager.SpawnedDesignationsOfDef(def).Select(d => d.target.HasThing ? new GlobalTargetInfo(d.target.Thing) : new GlobalTargetInfo(d.target.Cell, map));
        }

        static IEnumerable<GlobalTargetInfo> Things(IEnumerable<Thing> things) => things.Where(t => t != null).Select(t => new GlobalTargetInfo(t));

        // The work type that currently owns a job: the first of the named work givers that exists decides.
        static string TypeOf(string fallback, params string[] workGivers)
        {
            string res = fallback;
            foreach (string name in workGivers)
            {
                WorkGiverDef wg = DefDatabase<WorkGiverDef>.GetNamedSilentFail(name);
                if (wg?.workType != null) { res = wg.workType.defName; break; }
            }
            Countable.Add(res);
            FieldKeys.Add(res);
            return res;
        }

        // Work types this page is able to count (filled as the hourly count runs).
        public static readonly HashSet<string> Countable = new HashSet<string>();
        static readonly HashSet<string> FieldKeys = new HashSet<string>();   // counted as items or orders on the map
        static readonly HashSet<string> BillKeys = new HashSet<string>();    // counted as open workbench bills

        public static List<string> BehindNames() { Ensure(); return lines.Where(l => l.level >= 3).Select(l => l.wt.labelShort.CapitalizeFirst() + " (" + l.status.ToLower() + ", " + l.now + " waiting)").ToList(); }

        static void Try(Action a) { try { a(); } catch { } }

        public static Dictionary<ThingDef, WorkTypeDef> EnsureBillTypes()
        {
            if (billTypes == null)
            {
                billTypes = new Dictionary<ThingDef, WorkTypeDef>();
                foreach (WorkGiverDef wg in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                    if (wg.fixedBillGiverDefs != null && wg.workType != null)
                        foreach (ThingDef td in wg.fixedBillGiverDefs)
                            if (td != null && !billTypes.ContainsKey(td)) billTypes[td] = wg.workType;
            }
            return billTypes;
        }

        // How much work of each type is waiting right now, across home maps. Keys are WorkTypeDef names.
        public static Dictionary<string, int> Count() => Gather(null, null);

        // The actual things and cells counted for one work type, for "show me where".
        public static List<GlobalTargetInfo> Targets(string workType)
        {
            var list = new List<GlobalTargetInfo>();
            Gather(workType, list);
            return list;
        }

        static Dictionary<string, int> Gather(string wantKey, List<GlobalTargetInfo> into)
        {
            EnsureBillTypes();
            var d = new Dictionary<string, int>();
            Faction player = Faction.OfPlayerSilentFail;
            void Put(string key, IEnumerable<GlobalTargetInfo> items)
            {
                if (wantKey != null && wantKey != key) { if (into != null) return; }
                int n = 0;
                foreach (GlobalTargetInfo g in items) { n++; if (into != null && wantKey == key) into.Add(g); }
                Bump(d, key, n);
            }
            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                // Each count is filed under whichever work type currently owns the job that does it, so mods that
                // split or move work (Complex Jobs: Repair, Deconstruct, Harvest, Nurse...) are followed automatically.
                Try(() => Put(TypeOf("Hauling", "HaulGeneral"), Things(map.listerHaulables.ThingsPotentiallyNeedingHauling())));
                Try(() => Put(TypeOf("Cleaning", "CleanFilth"), Things(map.listerFilthInHomeArea.FilthInHomeArea)));
                Try(() => Put(TypeOf("Construction", "ConstructDeliverResourcesToBlueprints"), Things(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Where(t => t.Faction == player))));
                Try(() => Put(TypeOf("Construction", "ConstructFinishFrames"), Things(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Where(t => t.Faction == player))));
                // colonists only repair inside the home area, so damage outside it is not waiting work
                Try(() => Put(TypeOf("Construction", "Repair"), Things(map.listerBuildingsRepairable.RepairableBuildings(player).Where(b => b.Spawned && map.areaManager.Home[b.Position]
                    // the game's list also holds things with no hit points at all (spots); nobody can repair those
                    && b.def.useHitPoints && b.HitPoints < b.MaxHitPoints && !b.IsBurning()
                    && map.designationManager.DesignationOn(b, DesignationDefOf.Deconstruct) == null && map.designationManager.DesignationOn(b, DesignationDefOf.Uninstall) == null))));
                Try(() => Put(TypeOf("Construction", "Deconstruct"), Designated(map, "Deconstruct")));
                Try(() => Put(TypeOf("Construction", "Uninstall"), Designated(map, "Uninstall")));
                Try(() => Put(TypeOf("Construction", "ConstructSmoothFloor", "SmoothFloor"), Designated(map, "SmoothFloor")));
                Try(() => Put(TypeOf("Construction", "ConstructSmoothWall", "SmoothWall"), Designated(map, "SmoothWall")));
                Try(() => Put(TypeOf("Construction", "ConstructRemoveFloors", "ConstructRemoveFloor", "RemoveFloor"), Designated(map, "RemoveFloor")));
                Try(() => Put(TypeOf("Mining", "Mine"), Designated(map, "Mine").Concat(Designated(map, "MineVein"))));
                Try(() => Put(TypeOf("PlantCutting", "PlantsCut"), Designated(map, "CutPlant").Concat(Designated(map, "HarvestPlant"))));
                Try(() => Put(TypeOf("Hunting", "HunterHunt"), Designated(map, "Hunt")));
                Try(() => Put(TypeOf("Handling", "Tame"), Designated(map, "Tame")));
                Try(() => Put(TypeOf("Doctor", "DoctorTendToHumanlikes"), Things(map.mapPawns.AllPawnsSpawned.Where(p => (p.Faction == player || p.IsPrisonerOfColony) && p.health != null && p.health.HasHediffsNeedingTend()))));
                Try(() =>
                {
                    var sow = new List<GlobalTargetInfo>();
                    var harvest = new List<GlobalTargetInfo>();
                    foreach (Zone z in map.zoneManager.AllZones)
                    {
                        if (!(z is Zone_Growing zg)) continue;
                        ThingDef crop = zg.GetPlantDefToGrow();
                        foreach (IntVec3 c in zg.Cells)
                        {
                            Plant pl = c.GetPlant(map);
                            if (pl == null) { if (zg.allowSow && crop != null && PlantUtility.GrowthSeasonNow(c, map, crop)) sow.Add(new GlobalTargetInfo(c, map)); }
                            else if (pl.def == crop && pl.HarvestableNow) harvest.Add(new GlobalTargetInfo(pl));
                        }
                    }
                    Put(TypeOf("Growing", "GrowerSow"), sow);
                    Put(TypeOf("Growing", "GrowerHarvest"), harvest);
                });
                Try(() =>
                {
                    foreach (Building b in map.listerBuildings.allBuildingsColonist)
                    {
                        if (!(b is IBillGiver giver) || giver.BillStack == null || !billTypes.TryGetValue(b.def, out WorkTypeDef wt)) continue;
                        Countable.Add(wt.defName);
                        BillKeys.Add(wt.defName);
                        HrSettings hr = HumanResourcesMod.Hr;
                        if (hr != null && hr.excludedBenches.Contains(b.def.defName)) continue;      // the player said this bench isn't workload
                        int n = 0;
                        foreach (Bill bill in giver.BillStack.Bills)
                            try { if (!bill.suspended && bill.ShouldDoNow() && (hr == null || bill.recipe == null || !hr.excludedRecipes.Contains(bill.recipe.defName))) n++; } catch { }
                        if (n > 0) Put(wt.defName, Enumerable.Repeat(new GlobalTargetInfo(b), n).Take(into != null ? 1 : n));
                    }
                });
            }
            return d;
        }

        // Menu for a workbench row: leave a bill or a kind of bench out of the workload analysis.
        static void ExcludeMenu(Line l)
        {
            HrSettings hr = HumanResourcesMod.Hr;
            if (hr == null) return;
            var opts = new List<FloatMenuOption>();
            var seenRecipes = new HashSet<string>();
            var seenBenches = new HashSet<string>();
            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (!(b is IBillGiver giver) || giver.BillStack == null || !EnsureBillTypes().TryGetValue(b.def, out WorkTypeDef wt) || wt != l.wt) continue;
                    if (hr.excludedBenches.Contains(b.def.defName)) continue;
                    bool any = false;
                    foreach (Bill bill in giver.BillStack.Bills)
                    {
                        bool open = false;
                        try { open = !bill.suspended && bill.ShouldDoNow(); } catch { }
                        if (!open || bill.recipe == null || hr.excludedRecipes.Contains(bill.recipe.defName)) continue;
                        any = true;
                        RecipeDef recipe = bill.recipe;
                        if (seenRecipes.Add(recipe.defName))
                            opts.Add(new FloatMenuOption("Leave out the bill: " + recipe.LabelCap, () => { hr.excludedRecipes.Add(recipe.defName); AfterExclusionChange(); }));
                    }
                    ThingDef bench = b.def;
                    if (any && seenBenches.Add(bench.defName))
                        opts.Add(new FloatMenuOption("Leave out every bill at: " + bench.LabelCap, () => { hr.excludedBenches.Add(bench.defName); AfterExclusionChange(); }));
                }
            }
            opts.Add(new FloatMenuOption("Manage what is left out...", () => Find.WindowStack.Add(new Dialog_ModSettings(HumanResourcesMod.Instance))));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        public static void AfterExclusionChange()
        {
            try { HumanResourcesMod.Instance?.WriteSettings(); } catch { }
            if (Current.ProgramState == ProgramState.Playing) TimeUseTracker.Instance?.SampleBacklog();     // recount this hour without the excluded bills
            stamp = -1;
            JobAdvisor.Invalidate();
        }

        // ---- "show me where": step through the things counted for a work type ----

        static string cycleKey, cycleInfo;
        static int cycleIndex;

        const string Fine = "nothing obviously wrong";

        static string Diagnose(GlobalTargetInfo g, string key)
        {
            Map map = g.Map;
            if (map == null) return "gone";
            var notes = new List<string>();
            Thing th = g.HasThing ? g.Thing : null;
            IntVec3 cell = g.Cell;
            try { if (th != null && th.IsForbidden(Faction.OfPlayer)) notes.Add("forbidden"); } catch { }
            try { if (!map.reachability.CanReachColony(cell)) notes.Add("no path from the colony"); } catch { }
            try { if (cell.Fogged(map)) notes.Add("in fog"); } catch { }
            try
            {
                if (th != null && key == TypeOf("Hauling", "HaulGeneral") && th.def.EverHaulable
                    && !StoreUtility.TryFindBestBetterStoreCellFor(th, null, map, StoreUtility.CurrentStoragePriorityOf(th), Faction.OfPlayer, out IntVec3 _, false))
                    notes.Add("no stockpile or shelf with room accepts it");
            }
            catch { }
            if (notes.Count > 0) return string.Join(", ", notes);
            try { if (th != null && map.reservationManager.IsReservedByAnyoneOf(th, Faction.OfPlayer)) return Fine + " (a colonist has claimed it and is on the way)"; } catch { }
            return Fine;
        }

        static void Cycle(Line l, int step)
        {
            List<GlobalTargetInfo> targets = Targets(l.wt.defName).Where(g => g.IsValid && g.Map != null).ToList();
            if (targets.Count == 0) { cycleKey = l.wt.defName; cycleInfo = l.wt.labelShort.CapitalizeFirst() + ": nothing to show right now."; return; }
            if (cycleKey != l.wt.defName) { cycleKey = l.wt.defName; cycleIndex = step > 0 ? -1 : 0; }
            cycleIndex = ((cycleIndex + step) % targets.Count + targets.Count) % targets.Count;
            GlobalTargetInfo g = targets[cycleIndex];
            string name = g.HasThing ? g.Thing.LabelCap : "Cell";
            string what = Diagnose(g, l.wt.defName);
            // quick tally over the whole pile (first 300) so the pattern is visible without clicking through them all
            int blocked = 0, sample = Mathf.Min(150, targets.Count);
            for (int i = 0; i < sample; i++) if (!Diagnose(targets[i], l.wt.defName).StartsWith(Fine)) blocked++;
            cycleInfo = l.wt.labelShort.CapitalizeFirst() + " " + (cycleIndex + 1) + " of " + targets.Count + ": " + name + " at " + g.Cell + " - " + what
                + ".   Overall: " + blocked + " of " + sample + " have a visible problem.";
            if (g.HasThing) CameraJumper.TryJumpAndSelect(g); else CameraJumper.TryJump(g);
        }

        static void Analyze()
        {
            lines.Clear();
            hot.Clear();
            levels.Clear();
            unattended.Clear();
            TimeUseTracker tracker = TimeUseTracker.Instance;
            if (tracker == null) return;
            if (tracker.backlog.Count == 0) tracker.SampleBacklog();
            else if (Countable.Count == 0) Count();     // after loading: learn which types are bills before judging them
            int hour = Find.TickManager.TicksGame / GenDate.TicksPerHour;
            int latest = tracker.backlog.Keys.Count == 0 ? hour : tracker.backlog.Keys.Max();
            var keys = new HashSet<string>();
            foreach (var h in tracker.backlog) if (h.Key > hour - 48) foreach (string k in h.Value.Keys) keys.Add(k);
            foreach (string key in keys)
            {
                WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail(key);
                if (wt == null) continue;
                int At(int h) => tracker.backlog.TryGetValue(h, out var d) ? (d.TryGetValue(key, out int v) ? v : 0) : -1;
                Line l = new Line { wt = wt, now = Mathf.Max(0, At(latest)), history = new int[48] };
                for (int i = 0; i < 48; i++) l.history[i] = At(latest - 47 + i);
                l.dayAgo = -1;
                for (int h = latest - 48; h < latest - 24 && l.dayAgo < 0; h++) l.dayAgo = At(h);     // the count about two days ago (-1 until there is that much history)
                for (int h = latest; h > latest - TimeUseTracker.HoursKept; h--) { if (At(h) > 0) l.streak++; else break; }
                l.labourHours = (float)tracker.ColonyTicks("wt:" + key, 24) / GenDate.TicksPerHour;

                // Open bills are not a pile of work: a "do forever" bill is always open. A steady number of open bills
                // is normal; it only matters when the number grows, or when nobody has touched them for a day.
                bool billsOnly = BillKeys.Contains(key) && !FieldKeys.Contains(key);
                l.bills = billsOnly;
                bool growing = l.dayAgo >= 0 && l.now > l.dayAgo * 1.25f && l.now - l.dayAgo >= 2;
                if (l.now == 0) { l.level = 0; l.status = "Clear"; }
                else if (billsOnly && !growing && l.labourHours < 0.5f && l.streak >= 24) { l.level = 2; l.status = "Stuck bills"; l.stuck = true; }
                else if (billsOnly && !growing) { l.level = 1; l.status = "Open bills"; }
                // Judged over two days, because work arrives in bursts (a field ripening overnight) and a pile that
                // appeared while people slept is not neglect. It is only a worry when nobody has worked on it for a
                // day, or when two full days of work haven't brought it down.
                else if (l.streak >= 24 && l.labourHours < 0.25f) { l.level = 4; l.status = "Not being worked"; unattended.Add(key); }
                else if (l.streak >= 48 && l.dayAgo >= 0 && l.now >= l.dayAgo * 0.9f) { l.level = 4; l.status = "Needs more workers"; }
                else if (l.streak >= 24 && l.dayAgo >= 0 && l.now > l.dayAgo * 1.25f && l.now - l.dayAgo >= 5) { l.level = 3; l.status = "Falling behind"; }
                else if (l.streak >= 24) { l.level = 2; l.status = "Slow"; }
                else { l.level = 1; l.status = "Keeping up"; }
                if (l.level >= 3) hot.Add(key);
                levels[key] = l.level;
                lines.Add(l);
            }
            lines.SortByDescending(l => l.level * 100000 + Mathf.Min(l.now, 99999));
        }

        static void Ensure()
        {
            int now = Find.TickManager.TicksGame / 250;
            if (stamp != now) { stamp = now; try { Analyze(); } catch (Exception e) { Log.ErrorOnce(HR.Tag + "Workload analysis failed: " + e, 81730063); } }
        }

        // Work types that are falling behind or never get cleared.
        public static HashSet<string> Hot() { Ensure(); return hot; }

        static Color LevelColor(int level)
        {
            switch (level)
            {
                case 4: return new Color(0.90f, 0.30f, 0.28f);
                case 3: return new Color(0.95f, 0.60f, 0.25f);
                case 2: return new Color(0.92f, 0.85f, 0.40f);
                case 1: return new Color(0.45f, 0.80f, 0.45f);
                default: return new Color(0.6f, 0.6f, 0.6f);
            }
        }

        public static void Draw(Rect inRect)
        {
            Ensure();
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Rect undoRect = new Rect(inRect.x, inRect.y, 150f, 28f);
            if (Widgets.ButtonText(undoRect, "Undo last change", true, true, JobAdvisor.CanUndo)) { JobAdvisor.UndoLast(); stamp = -1; }
            GUI.color = Color.gray;
            TimeUseTracker trk = TimeUseTracker.Instance;
            if (cycleInfo != null)
            {
                GUI.color = new Color(1f, 0.9f, 0.6f);
                Widgets.Label(new Rect(undoRect.xMax + 14f, inRect.y, inRect.width - 170f, 28f), cycleInfo);
            }
            else if (trk != null && trk.resting.Count > 0)
                Widgets.Label(new Rect(undoRect.xMax + 14f, inRect.y, inRect.width - 170f, 28f), "Resting (switched off until work appears): "
                    + string.Join(", ", trk.resting.Keys.Select(k => DefDatabase<WorkTypeDef>.GetNamedSilentFail(k)?.labelShort.CapitalizeFirst() ?? k)));
            else
            Widgets.Label(new Rect(undoRect.xMax + 14f, inRect.y, inRect.width - 170f, 28f),
                "Counted once an in-game hour on your home maps, and filed under whichever work type owns each job in your mod list (so split types like Repair, Harvest or Fabricate get their own row).");
            GUI.color = Color.white;
            float y = inRect.y + 34f;

            if (lines.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 60f), "Nothing is waiting, or no samples yet. The first full picture needs about a day of play.");
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            float[] w = { 150f, 120f, 70f, 70f, 110f, 90f, 60f, 200f };
            string[] head = { "Work", "Status", "Waiting", "2 days ago", "Not cleared for", "Labour (24h)", "On it", "Last 48 hours" };
            string[] tips =
            {
                null, "Clear: nothing waiting.\nKeeping up: some waiting, including a fresh pile less than a day old.\nSlow: something has been waiting for a full day, but people are working on it.\nFalling behind: waiting for a day and 25% more than two days ago, despite work on it. More workers are suggested.\nNeeds more workers: two days of steady work haven't brought it down. More workers are suggested.\nNot being worked: waiting for a day and nobody worked on it at all. The people assigned never get to it, so the suggestion changes who has it as their first job.\n\nWorkbench work counts open bills, not items:\nOpen bills: a steady number of bills that are being worked.\nStuck bills: bills open for a day that nobody worked on. That is usually missing materials or a bill setting, not staffing.",
                "Items, jobs or bills waiting at the last hourly count.", "The count about two days earlier.", "How long there has been something waiting without a break.",
                "Colonist-hours actually spent on this work in the last 24 hours.", "Colonists who have this work at priority 1 or 2.", "Waiting work at each hourly count; the right edge is now."
            };
            const float rowH = 30f;
            float x = inRect.x;
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            for (int i = 0; i < head.Length; i++)
            {
                Rect h = new Rect(x + 4f, y, w[i] - 4f, 20f);
                Widgets.Label(h, head[i]);
                if (tips[i] != null) TooltipHandler.TipRegion(h, tips[i]);
                x += w[i];
            }
            float sugX = x;
            Widgets.Label(new Rect(sugX + 4f, y, inRect.xMax - sugX - 4f, 20f), "Suggestion");
            GUI.color = Color.white;
            y += 22f;

            Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, lines.Count * rowH);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            for (int i = 0; i < lines.Count; i++)
            {
                Line l = lines[i];
                float ry = i * rowH;
                if (i % 2 == 1) Widgets.DrawLightHighlight(new Rect(0f, ry, view.width, rowH));
                x = 0f;
                Text.Font = GameFont.Small;
                Rect workRect = new Rect(x + 4f, ry, w[0] - 4f, rowH);
                if (cycleKey == l.wt.defName) Widgets.DrawHighlightSelected(workRect);
                Widgets.DrawHighlightIfMouseover(workRect);
                Widgets.Label(workRect, l.wt.labelShort.CapitalizeFirst()); x += w[0];
                TooltipHandler.TipRegion(workRect, "Click to jump to the next thing counted here; right-click for the previous one. Each jump says what looks wrong with it (forbidden, no path, nowhere to store it...).");
                if (Mouse.IsOver(workRect) && Event.current.type == EventType.MouseDown)
                {
                    try { Cycle(l, Event.current.button == 1 ? -1 : 1); } catch (Exception e) { cycleInfo = "Couldn't show that: " + e.Message; }
                    Event.current.Use();
                }
                GUI.color = LevelColor(l.level);
                Widgets.Label(new Rect(x + 4f, ry, w[1] - 4f, rowH), l.status); x += w[1];
                GUI.color = Color.white;
                Widgets.Label(new Rect(x + 4f, ry, w[2] - 4f, rowH), l.now.ToString()); x += w[2];
                Widgets.Label(new Rect(x + 4f, ry, w[3] - 4f, rowH), l.dayAgo < 0 ? "-" : l.dayAgo.ToString()); x += w[3];
                Widgets.Label(new Rect(x + 4f, ry, w[4] - 4f, rowH), l.streak == 0 ? "-" : l.streak >= TimeUseTracker.HoursKept ? "3+ days" : l.streak >= 24 ? (l.streak / 24f).ToString("0.#") + " days" : l.streak + " h"); x += w[4];
                Widgets.Label(new Rect(x + 4f, ry, w[5] - 4f, rowH), l.labourHours.ToString("0.#") + " h"); x += w[5];
                Widgets.Label(new Rect(x + 4f, ry, w[6] - 4f, rowH), JobAdvisor.WorkersOn(l.wt).ToString()); x += w[6];

                // history bars
                Rect spark = new Rect(x + 4f, ry + 4f, w[7] - 12f, rowH - 8f);
                Widgets.DrawBoxSolid(spark, new Color(0.10f, 0.10f, 0.10f));
                int max = Mathf.Max(1, l.history.Max());
                float bw = spark.width / l.history.Length;
                Color bar = LevelColor(l.level);
                for (int k = 0; k < l.history.Length; k++)
                {
                    if (l.history[k] <= 0) continue;
                    float bh = Mathf.Max(1f, spark.height * l.history[k] / max);
                    Widgets.DrawBoxSolid(new Rect(spark.x + k * bw, spark.yMax - bh, Mathf.Max(1f, bw - 1f), bh), bar);
                }
                TooltipHandler.TipRegion(spark, "Highest in this period: " + max);
                x += w[7];

                if (l.stuck)
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(x + 4f, ry, view.width - x - 108f, rowH), "Nobody worked these bills in a day: missing materials, or bills with nothing to do right now.");
                    GUI.color = Color.white;
                }
                if (l.bills && (l.stuck || l.level < 2))
                {
                    Rect ex = new Rect(view.width - 96f, ry + 3f, 92f, rowH - 6f);
                    Text.Font = GameFont.Tiny;
                    if (Widgets.ButtonText(ex, "Leave out...")) ExcludeMenu(l);
                    TooltipHandler.TipRegion(ex, "Leave a bill or a kind of workbench out of the workload analysis. Use it for standing bills that only have work now and then, like recycling. Manage the list in Options, Mod settings, HR manager.");
                }
                if (l.stuck) { }
                else if (l.level >= 2)
                {
                    // the same suggestions the Job advisor makes for this work type
                    var sugg = JobAdvisor.Suggested(l.wt, 1);   // one colonist at a time; the next one shows after applying
                    if (sugg.Count > 0)
                    {
                        Rect btn = new Rect(view.width - 96f, ry + 3f, 92f, rowH - 6f);
                        Text.Font = GameFont.Tiny;
                        Rect lab = new Rect(x + 4f, ry, btn.x - x - 8f, rowH);
                        Widgets.Label(lab, string.Join(", ", sugg.Select(g => g.pawn.LabelShortCap + " " + (g.cur == 0 ? "off" : g.cur.ToString()) + ">" + g.sug)));
                        TooltipHandler.TipRegion(lab, "What the Job advisor suggests for this work: who to raise, and to which priority. Unskilled work is spread so each neglected job gets its own person at priority 1; skilled work goes to colonists with the skill or a passion for it, below any stronger skill they have.");
                        if (Widgets.ButtonText(btn, "Apply")) { JobAdvisor.ApplyCells(l.wt, sugg.Select(g => g.pawn).ToList()); stamp = -1; }
                    }
                    else
                    {
                        Text.Font = GameFont.Tiny;
                        GUI.color = Color.gray;
                        Widgets.Label(new Rect(x + 4f, ry, view.width - x - 8f, rowH), "Nobody else suitable to add. Look at walking distance, tools or materials.");
                        GUI.color = Color.white;
                    }
                }
            }
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
