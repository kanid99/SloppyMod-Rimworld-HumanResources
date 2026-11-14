using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace SloppyHR
{
    // "Job titles" page. A job title is a named template of work priorities the player creates and assigns
    // to colonists. The page also suggests who suits a title best and flags holders who aren't doing its work.

    public class JobTitle : IExposable
    {
        public int id;
        public string name = "New title";
        public Dictionary<string, int> prio = new Dictionary<string, int>();
        List<string> packed;

        public int Get(WorkTypeDef wt) => prio.TryGetValue(wt.defName, out int v) ? v : 0;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref name, "name", "Title");
            if (Scribe.mode == LoadSaveMode.Saving) packed = prio.Select(kv => kv.Key + ";" + kv.Value).ToList();
            Scribe_Collections.Look(ref packed, "prio", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                prio = new Dictionary<string, int>();
                if (packed != null)
                    foreach (string line in packed)
                    {
                        string[] p = line.Split(';');
                        if (p.Length == 2 && int.TryParse(p[1], out int v)) prio[p[0]] = v;
                    }
                packed = null;
            }
        }
    }

    public static class JobTitles
    {
        static int selectedId = -1;
        static Vector2 listScroll, bodyScroll;

        // ---- logic ----

        static readonly float[] Weight = { 0f, 3f, 2f, 1f, 0f };     // how much a priority 1 / 2 / 3 job counts toward fit

        // How well a colonist suits a title: their fit for its skilled jobs, weighted by the title's priorities.
        // Returns -1 for a title with no skilled work at priority 1-3 (an unskilled title).
        public static float Fit(JobTitle title, Pawn p)
        {
            float sum = 0f, w = 0f;
            foreach (WorkTypeDef wt in JobAdvisor.Types)
            {
                int pr = title.Get(wt);
                if (pr < 1 || pr > 3 || !JobAdvisor.IsSkilled(wt)) continue;
                w += Weight[pr];
                sum += Weight[pr] * JobAdvisor.Score(p, wt);
            }
            return w <= 0f ? -1f : sum / w;
        }

        // True while this mod is writing priorities itself, so those writes aren't mistaken for the player's hand edits.
        public static bool Applying;

        public static void ApplyTo(JobTitle title, Pawn p, Dictionary<string, int> overrides = null)
        {
            if (p?.workSettings == null || !p.workSettings.EverWork) return;
            if (!Find.PlaySettings.useWorkPriorities) Find.PlaySettings.useWorkPriorities = true;
            bool was = Applying;
            Applying = true;
            try
            {
                foreach (WorkTypeDef wt in JobAdvisor.Types)
                {
                    if (p.WorkTypeIsDisabled(wt)) continue;
                    int want = title.Get(wt);
                    if (overrides != null && overrides.TryGetValue(wt.defName, out int forced)) want = forced;      // a box the player set by hand wins
                    else if (want == 0 && JobAdvisor.IsFixed(wt)) continue;      // never switch off firefighting, patient and the like
                    if (p.workSettings.GetPriority(wt) != want) p.workSettings.SetPriority(wt, want);
                }
            }
            finally { Applying = was; }
        }

        // Sets a colonist's work priorities from all the titles they hold, merged, with their hand-set boxes on top.
        public static void Reapply(TimeUseTracker t, Pawn p)
        {
            JobTitle merged = t.TitleFor(p);
            if (merged != null) ApplyTo(merged, p, t.OverridesFor(p));
            JobAdvisor.Invalidate();
        }

        // The player changed a title holder's box by hand (on the Work tab or in the Colonists view).
        public static void NoteHandEdit(Pawn p, WorkTypeDef wt, int priority)
        {
            TimeUseTracker t = TimeUseTracker.Instance;
            JobTitle merged = t?.TitleFor(p);
            if (merged == null || wt == null) return;
            int fromTitles = merged.Get(wt);
            if (fromTitles == 0 && JobAdvisor.IsFixed(wt)) return;        // titles don't control these when left off
            if (priority == fromTitles) t.ClearOverride(p, wt.defName);   // set back to what the titles say
            else t.SetOverride(p, wt.defName, priority);
            JobAdvisor.Invalidate();
        }

        // Makes this the colonist's only title.
        public static void Assign(TimeUseTracker t, JobTitle title, Pawn p)
        {
            t.SetOnlyTitle(p, title);
            Reapply(t, p);
        }

        // Adds the title after the ones the colonist already holds. False when they already hold the maximum.
        public static bool Add(TimeUseTracker t, JobTitle title, Pawn p)
        {
            if (!t.AddTitle(p, title))
            {
                Messages.Message(p.LabelShortCap + " already holds " + TimeUseTracker.MaxTitles + " titles. Remove one first.", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            Reapply(t, p);
            return true;
        }

        public static void Remove(TimeUseTracker t, JobTitle title, Pawn p)
        {
            t.RemoveTitle(p, title);
            Reapply(t, p);      // priorities stay as they are when it was their last title
        }

        public static void MakeFirst(TimeUseTracker t, JobTitle title, Pawn p)
        {
            t.MakeFirst(p, title);
            Reapply(t, p);
        }

        public static void ClearAll(TimeUseTracker t, Pawn p)
        {
            t.ClearTitles(p);
            JobAdvisor.Invalidate();
        }

        static List<Pawn> Holders(TimeUseTracker t, JobTitle title, List<Pawn> pawns) => pawns.Where(p => t.HasTitle(p, title)).ToList();

        // One line on how a title holder is doing, and whether it is a concern.
        static string Review(TimeUseTracker t, JobTitle title, Pawn p, float fit, Pawn bestOther, float bestOtherFit, out bool concern)
        {
            concern = false;
            int work = 0, main = 0, idle = 0, total = 0;
            var mainTypes = new HashSet<string>(JobAdvisor.Types.Where(w => { int pr = title.Get(w); return pr == 1 || pr == 2; }).Select(w => "wt:" + w.defName));
            t.Visit(p, 48, (key, ticks) =>
            {
                total += ticks;
                if (key == "idle") idle += ticks;
                if (key.StartsWith("wt:")) { work += ticks; if (mainTypes.Contains(key)) main += ticks; }
            });
            var notes = new List<string>();
            if (total < GenDate.TicksPerHour * 6) notes.Add("not enough time tracked yet");
            else
            {
                float idleShare = (float)idle / total;
                if (mainTypes.Count > 0 && work >= GenDate.TicksPerHour * 2)
                {
                    float share = (float)main / work;
                    if (share < 0.25f) { concern = true; notes.Add("only " + share.ToStringPercent("0") + " of their work time went to this title's priority 1-2 jobs"); }
                    else notes.Add(share.ToStringPercent("0") + " of their work time on this title's priority 1-2 jobs");
                }
                else if (mainTypes.Count > 0) { concern = true; notes.Add("did almost no work in the last 2 days"); }
                if (idleShare >= 0.25f) { concern = true; notes.Add("idle " + idleShare.ToStringPercent("0") + " of the time"); }
            }
            if (fit >= 0f)
            {
                if (fit < 5f) { concern = true; notes.Add("weak fit for this title's skilled work"); }
                if (bestOther != null && bestOtherFit > fit * 1.5f && bestOtherFit - fit >= 3f) { concern = true; notes.Add(bestOther.LabelShortCap + " would suit it much better (fit " + bestOtherFit.ToString("0.0") + ")"); }
            }
            return notes.Count == 0 ? "-" : string.Join("; ", notes).CapitalizeFirst() + ".";
        }

        // The review line for one title holder, for use outside this page.
        public static string ReviewFor(TimeUseTracker t, Pawn p, out bool concern)
        {
            concern = false;
            JobTitle title = t?.TitleFor(p);
            if (title == null) return null;
            List<Pawn> pawns = JobAdvisor.Pawns;
            float fit = Fit(title, p);
            // "someone else would suit it better" only makes sense for a single title, not for a merged set
            bool single = t.TitleCount(p) == 1;
            Pawn bestOther = fit < 0f || !single ? null : pawns.Where(o => !t.HasTitle(o, title)).OrderByDescending(o => Fit(title, o)).FirstOrDefault();
            return Review(t, title, p, fit, bestOther, bestOther != null ? Fit(title, bestOther) : 0f, out concern);
        }

        // ---- library: titles kept in the mod's settings file, outside any save ----

        static void WriteLibrary()
        {
            try { HumanResourcesMod.Instance?.WriteSettings(); }
            catch (Exception e) { Messages.Message("Could not write the job title library: " + e.Message, MessageTypeDefOf.RejectInput, false); }
        }

        static void SaveToLibrary(JobTitle title, JobTitle replace)
        {
            HrSettings hr = HumanResourcesMod.Hr;
            if (hr == null) return;
            if (replace != null) hr.titleLibrary.Remove(replace);
            hr.titleLibrary.Add(new JobTitle { id = 0, name = title.name, prio = new Dictionary<string, int>(title.prio) });
            WriteLibrary();
            Messages.Message("Saved \"" + title.name + "\" to the job title library.", MessageTypeDefOf.TaskCompletion, false);
        }

        static void LoadFromLibrary(TimeUseTracker t, JobTitle src)
        {
            string name = src.name;
            for (int n = 2; t.titles.Any(x => x.name == name); n++) name = src.name + " " + n;
            JobTitle nt = new JobTitle { id = t.nextTitleId++, name = name };
            var missing = new List<string>();
            foreach (var kv in src.prio)
            {
                if (DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv.Key) != null) nt.prio[kv.Key] = kv.Value;
                else missing.Add(kv.Key);
            }
            t.titles.Add(nt);
            selectedId = nt.id;
            if (missing.Count > 0)
                Find.WindowStack.Add(new Dialog_MessageBox("Loaded \"" + name + "\", but this game doesn't have " + missing.Count + " of its work types, so they were left out:\n\n" + string.Join(", ", missing)
                    + "\n\nThey usually come from a mod that isn't active here. The library copy still has them."));
            else Messages.Message("Loaded \"" + name + "\" from the library. Nobody is assigned to it yet.", MessageTypeDefOf.TaskCompletion, false);
        }

        // ---- drawing ----

        const float CellW = 26f, HeaderH = 84f, HeaderOverhang = 76f;
        // window width that fits the title list plus every work type in a single row
        public static float WantedWidth => 244f + JobAdvisor.Types.Count * CellW + HeaderOverhang;

        static readonly Color[] SourceColors = { new Color(0.40f, 0.75f, 1.00f), new Color(1.00f, 0.72f, 0.30f), new Color(0.80f, 0.55f, 0.95f), new Color(0.55f, 0.90f, 0.50f), new Color(1.00f, 0.50f, 0.60f) };
        static readonly Color HandColor = new Color(1f, 1f, 1f);

        // One row of work-priority boxes laid out like the Work tab (wraps if the width is too small). Returns the height used.
        // enabled: null = all; mark: a coloured bar under the box; outlined: a white frame around it.
        static float DrawStrip(float x, float y, float width, List<WorkTypeDef> types, Func<WorkTypeDef, int> get, Func<WorkTypeDef, bool> enabled,
            Func<WorkTypeDef, Color?> mark, Func<WorkTypeDef, bool> outlined, Action<WorkTypeDef, int, int> click, Func<WorkTypeDef, string> tip, Pawn pawn = null)
        {
            int perStrip = Mathf.Clamp(Mathf.FloorToInt((width - HeaderOverhang) / CellW), 1, Mathf.Max(1, types.Count));
            int strips = Mathf.CeilToInt(types.Count / (float)perStrip);
            float stripH = HeaderH + CellW + (pawn != null ? 26f : 12f);     // room for the skill level under each box
            for (int i = 0; i < types.Count; i++)
            {
                WorkTypeDef wt = types[i];
                float cx = x + (i % perStrip) * CellW, top = y + (i / perStrip) * stripH;
                Rect box = new Rect(cx, top + HeaderH, 25f, 25f);
                int pr = get(wt);
                bool on = enabled == null || enabled(wt);
                bool unskilled = !JobAdvisor.IsSkilled(wt) && !JobAdvisor.IsFixed(wt);

                // slanted column label
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;
                Text.WordWrap = false;
                Vector2 pivot = new Vector2(cx + 12f, top + HeaderH - 4f);
                Matrix4x4 old = GUI.matrix;
                UI.RotateAroundPivot(-50f, pivot);
                GUI.color = !on ? new Color(1f, 1f, 1f, 0.25f) : pr == 0 ? new Color(1f, 1f, 1f, 0.45f) : unskilled ? new Color(0.85f, 0.80f, 0.60f) : Color.white;
                Widgets.Label(new Rect(pivot.x, pivot.y - 11f, 110f, 22f), wt.labelShort.CapitalizeFirst() + (JobAdvisor.IsFixed(wt) ? " *" : ""));
                GUI.matrix = old;
                Text.WordWrap = true;

                // the box
                GUI.color = on ? Color.white : new Color(1f, 1f, 1f, 0.25f);
                if (pawn != null && on && !DrawVanillaBackground(box, pawn, wt)) GUI.DrawTexture(box, WidgetsWork.WorkBoxBGTex_Mid);
                else if (pawn == null || !on) GUI.DrawTexture(box, WidgetsWork.WorkBoxBGTex_Mid);
                GUI.color = Color.white;
                if (pawn != null && on && !wt.relevantSkills.NullOrEmpty())
                {
                    // skill level under the box, brighter with a passion
                    float avg = pawn.skills.AverageOfRelevantSkillsFor(wt);
                    Passion ps = pawn.skills.MaxPassionOfRelevantSkillsFor(wt);
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.UpperCenter;
                    GUI.color = ps != Passion.None ? new Color(1f, 0.82f, 0.35f) : avg >= 8f ? Color.white : new Color(1f, 1f, 1f, 0.55f);
                    Widgets.Label(new Rect(box.x - 4f, box.yMax + 5f, box.width + 8f, 18f), avg.ToString("0") + (ps != Passion.None ? "*" : ""));
                    GUI.color = Color.white;
                }
                if (on && pr > 0)
                {
                    Text.Font = GameFont.Medium;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    GUI.color = WidgetsWork.ColorOfPriority(pr);
                    Widgets.Label(box.ContractedBy(-3f), pr.ToString());
                }
                GUI.color = Color.white;
                Color? m = on && mark != null ? mark(wt) : null;
                if (m.HasValue) Widgets.DrawBoxSolid(new Rect(box.x, box.yMax + 1f, box.width, 3f), m.Value);
                if (on && outlined != null && outlined(wt)) Widgets.DrawBox(box, 2);
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleLeft;
                if (Mouse.IsOver(box))
                {
                    Widgets.DrawHighlight(box);
                    string extra = tip?.Invoke(wt);
                    if (pawn != null && on)
                    {
                        string skillText = JobAdvisor.SkillText(pawn, wt);
                        string own = skillText == null ? "No skill involved." : JobAdvisor.IsSkilled(wt)
                            ? pawn.LabelShortCap + ": " + skillText + ". Advisor fit " + JobAdvisor.Score(pawn, wt).ToString("0.0") + "."
                            : pawn.LabelShortCap + ": " + skillText + ". The advisor treats this work as unskilled.";
                        extra = extra.NullOrEmpty() ? own : own + "\n\n" + extra;
                    }
                    TooltipHandler.TipRegion(box, wt.gerundLabel.CapitalizeFirst() + (unskilled ? " (unskilled)" : "")
                        + "\n" + wt.defName + ", from " + (wt.modContentPack?.Name ?? "unknown")
                        + (!on ? "\n\nThis colonist can't do this work." : "") + (extra.NullOrEmpty() ? "" : "\n\n" + extra));
                    if (on && click != null && Event.current.type == EventType.MouseDown)
                    {
                        int button = Event.current.button;
                        Event.current.Use();
                        click(wt, pr, button);
                    }
                }
            }
            return strips * stripH;
        }

        // The Work tab's own box background: shaded by skill, with the passion flame (and whatever other mods add to it).
        static System.Reflection.MethodInfo vanillaBackground;
        static bool vanillaBackgroundLooked;
        static bool DrawVanillaBackground(Rect box, Pawn pawn, WorkTypeDef wt)
        {
            if (!vanillaBackgroundLooked)
            {
                vanillaBackgroundLooked = true;
                try { vanillaBackground = HarmonyLib.AccessTools.Method(typeof(WidgetsWork), "DrawWorkBoxBackground", new[] { typeof(Rect), typeof(Pawn), typeof(WorkTypeDef) }); } catch { }
            }
            if (vanillaBackground == null) return false;
            try { vanillaBackground.Invoke(null, new object[] { box, pawn, wt }); GUI.color = Color.white; return true; }
            catch { vanillaBackground = null; GUI.color = Color.white; return false; }
        }

        // ---- Colonists view: one colonist's titles in order, and what they merge into ----

        const int FilterAll = -2, FilterNone = -1;
        static int filterTitleId = FilterAll;      // colonist list filter: everyone, no title, or a title id
        static int mode;                    // 0 = titles, 1 = colonists
        static int dragFrom = -1;
        static Vector2 pawnScroll;

        static void DrawColonists(Rect inRect, TimeUseTracker t, List<Pawn> pawns, List<WorkTypeDef> types)
        {
            // filter the list by the title held
            JobTitle filterTitle = filterTitleId >= 0 ? t.titles.FirstOrDefault(x => x.id == filterTitleId) : null;
            if (filterTitleId >= 0 && filterTitle == null) filterTitleId = FilterAll;      // that title was deleted
            Rect filterRect = new Rect(inRect.x, inRect.y, 230f, 26f);
            string filterLabel = filterTitleId == FilterAll ? "all colonists" : filterTitleId == FilterNone ? "no title" : filterTitle.name;
            if (Widgets.ButtonText(filterRect, ("Show: " + filterLabel).Truncate(220f)))
            {
                var opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption("All colonists (" + pawns.Count + ")", () => filterTitleId = FilterAll),
                    new FloatMenuOption("No title (" + pawns.Count(p => t.TitleFor(p) == null) + ")", () => filterTitleId = FilterNone),
                };
                foreach (JobTitle jt in t.titles.OrderBy(x => x.name))
                {
                    JobTitle pick = jt;
                    opts.Add(new FloatMenuOption(pick.name + " (" + pawns.Count(p => t.HasTitle(p, pick)) + ")", () => filterTitleId = pick.id));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(filterRect, "Show only colonists who hold a certain title (in any position), or only those with none.");

            Rect left = new Rect(inRect.x, inRect.y + 32f, 230f, inRect.height - 32f);
            List<Pawn> sorted = pawns.Where(p => filterTitleId == FilterAll || (filterTitleId == FilterNone ? t.TitleFor(p) == null : t.HasTitle(p, filterTitle)))
                .OrderBy(p => p.LabelShortCap).ToList();
            Rect listView = new Rect(0f, 0f, left.width - 16f, sorted.Count * 38f);
            if (HrSelection.TakeScroll(2))
            {
                int at = sorted.FindIndex(p => p.thingIDNumber == HrSelection.PawnId);
                if (at >= 0) pawnScroll.y = Mathf.Clamp(at * 38f - left.height / 2f + 38f, 0f, Mathf.Max(0f, listView.height - left.height));
            }
            Widgets.BeginScrollView(left, ref pawnScroll, listView);
            for (int i = 0; i < sorted.Count; i++)
            {
                Pawn p = sorted[i];
                Rect r = new Rect(0f, i * 38f, listView.width, 36f);
                if (p.thingIDNumber == HrSelection.PawnId) Widgets.DrawHighlightSelected(r);
                else Widgets.DrawHighlightIfMouseover(r);
                Text.Font = GameFont.Small;
                Text.WordWrap = false;
                Widgets.Label(new Rect(6f, r.y, r.width - 8f, 20f), p.LabelShortCap);
                Text.Font = GameFont.Tiny;
                JobTitle m = t.TitleFor(p);
                GUI.color = m == null ? new Color(1f, 1f, 1f, 0.35f) : new Color(0.75f, 0.85f, 1f);
                Widgets.Label(new Rect(6f, r.y + 18f, r.width - 8f, 18f), m == null ? "no title" : m.name.Truncate(r.width - 8f));
                GUI.color = Color.white;
                Text.WordWrap = true;
                if (Widgets.ButtonInvisible(r)) { HrSelection.Set(p.thingIDNumber); HrSelection.TakeScroll(2); HrSelection.SelectInGame(p); dragFrom = -1; }
            }
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
            if (sorted.Count == 0)
            {
                GUI.color = Color.gray;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(left.x + 6f, left.y, left.width - 12f, 40f), "Nobody matches this filter.");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            Rect right = new Rect(left.xMax + 14f, inRect.y, inRect.width - left.width - 14f, inRect.height);
            Pawn pawn = pawns.FirstOrDefault(p => p.thingIDNumber == HrSelection.PawnId);
            if (pawn == null)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(right.x, right.y, right.width, 90f),
                    "Pick a colonist to see the titles they hold, put them in order, and see the work priorities those titles add up to.\nA colonist can hold up to 5 titles. The first is their main job.");
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            float y = right.y;
            List<JobTitle> held = t.TitlesFor(pawn);
            Dictionary<string, int> hand = t.OverridesFor(pawn);

            // header
            Rect nameRect = new Rect(right.x, y, 220f, 28f);
            Text.Font = GameFont.Medium;
            Widgets.Label(nameRect, pawn.LabelShortCap);
            Text.Font = GameFont.Small;
            if (Widgets.ButtonInvisible(nameRect)) CameraJumper.TryJumpAndSelect(pawn);
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(right.x + 226f, y, right.width - 400f, 28f), "advisor role: " + JobAdvisor.RoleText(pawn));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (held.Count < TimeUseTracker.MaxTitles && Widgets.ButtonText(new Rect(right.xMax - 150f, y, 150f, 28f), "Add title..."))
            {
                var opts = t.titles.Where(x => !held.Contains(x)).Select(x => new FloatMenuOption(x.name, () => Add(t, x, pawn))).ToList();
                if (opts.Count == 0) opts.Add(new FloatMenuOption(t.titles.Count == 0 ? "No job titles yet. Make one in the Titles view." : "They already hold every title.", null));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            y += 36f;

            // titles in order
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(right.x, y, right.width, 20f), "TITLES   drag a row up or down to change the order. Every title is used as written. Where two titles set the same kind of work, the higher one wins.");
            GUI.color = Color.white;
            y += 22f;
            if (held.Count == 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(right.x, y, right.width, 28f), "No titles. The HR advisor manages this colonist's priorities.");
                GUI.color = Color.white;
                y += 30f;
            }
            // Drag to reorder: hold the mouse on a row and move it; the rows swap live as the cursor crosses them.
            float listTop = y;
            if (dragFrom >= held.Count) dragFrom = -1;
            if (dragFrom >= 0)
            {
                if (!Input.GetMouseButton(0)) dragFrom = -1;        // released (anywhere)
                else
                {
                    int target = Mathf.Clamp(Mathf.FloorToInt((Event.current.mousePosition.y - listTop) / 32f), 0, held.Count - 1);
                    if (target != dragFrom)
                    {
                        MoveTitle(t, pawn, dragFrom, target);
                        dragFrom = target;
                        held = t.TitlesFor(pawn);
                    }
                }
            }
            for (int i = 0; i < held.Count; i++)
            {
                JobTitle jt = held[i];
                Rect row = new Rect(right.x, y, Mathf.Min(right.width, 620f), 30f);
                bool dragging = dragFrom == i;
                Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, dragging ? 0.16f : 0.04f));
                Widgets.DrawBoxSolid(new Rect(row.x, row.y, 5f, row.height), SourceColors[i % SourceColors.Length]);
                if (dragging) Widgets.DrawBox(row, 1);
                else if (dragFrom < 0) Widgets.DrawHighlightIfMouseover(row);
                // grip
                if (held.Count > 1)
                {
                    Color grip = new Color(1f, 1f, 1f, dragging ? 0.9f : 0.35f);
                    for (int g = 0; g < 3; g++) Widgets.DrawBoxSolid(new Rect(row.x + 12f, row.y + 9f + g * 5f, 14f, 2f), grip);
                }
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(row.x + 36f, row.y, 30f, 30f), (i + 1) + ".");
                Widgets.Label(new Rect(row.x + 62f, row.y, row.width - 330f, 30f), jt.name);
                GUI.color = Color.gray;
                Text.Font = GameFont.Tiny;
                int lost = 0;       // kinds of work this title sets that a title above it also sets
                foreach (var kv in jt.prio)
                {
                    if (kv.Value <= 0) continue;
                    for (int above = 0; above < i; above++) if (held[above].prio.TryGetValue(kv.Key, out int pa) && pa > 0) { lost++; break; }
                }
                Widgets.Label(new Rect(row.xMax - 250f, row.y, 170f, 30f), lost == 0 ? "all used as written" : lost + " set by a title above");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                if (dragFrom < 0 && Widgets.ButtonText(new Rect(row.xMax - 70f, row.y + 3f, 68f, 24f), "Remove")) { Remove(t, jt, pawn); Text.Anchor = TextAnchor.UpperLeft; return; }
                Rect grab = new Rect(row.x, row.y, row.width - 76f, row.height);
                if (held.Count > 1 && dragFrom < 0 && Mouse.IsOver(grab))
                {
                    TooltipHandler.TipRegion(grab, "Hold and drag up or down to change the order.");
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 0) { dragFrom = i; Event.current.Use(); }
                }
                y += 32f;
            }
            y += 8f;

            // merged result
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(right.x, y, right.width - 200f, 20f), held.Count == 0
                ? "CURRENT WORK PRIORITIES   the same as the Work tab."
                : "WORK PRIORITIES THE GAME USES   boxes are shaded by skill as on the Work tab, with the skill level underneath (* = passion). The bar shows which title a box comes from; a white frame is a box you set by hand.");
            GUI.color = Color.white;
            int handCount = hand?.Count ?? 0;
            if (handCount > 0)
            {
                Rect clr = new Rect(right.xMax - 190f, y - 2f, 190f, 24f);
                if (Widgets.ButtonText(clr, "Clear hand-set boxes (" + handCount + ")")) { t.ClearOverrides(pawn); Reapply(t, pawn); Text.Anchor = TextAnchor.UpperLeft; return; }
                TooltipHandler.TipRegion(clr, "Go back to exactly what the titles say.");
            }
            y += 22f;
            JobTitle merged = t.TitleFor(pawn);
            y += DrawStrip(right.x, y, right.width, types,
                wt => pawn.workSettings.GetPriority(wt),
                wt => !pawn.WorkTypeIsDisabled(wt),
                wt =>
                {
                    if (merged == null || (hand != null && hand.ContainsKey(wt.defName))) return (Color?)null;
                    int v = merged.Get(wt);
                    if (v == 0) return null;
                    for (int i = 0; i < held.Count; i++) if (held[i].Get(wt) > 0) return SourceColors[i % SourceColors.Length];     // the first title that sets it
                    return null;
                },
                wt => hand != null && hand.ContainsKey(wt.defName),
                (wt, pr, button) =>
                {
                    int next = button == 1 ? (pr + 1) % 5 : (pr + 4) % 5;
                    pawn.workSettings.SetPriority(wt, next);      // a hand edit: remembered on top of the titles, like one made on the Work tab
                    if (!TitleOverrides.Patched) NoteHandEdit(pawn, wt, next);
                },
                wt =>
                {
                    if (merged == null) return null;
                    if (hand != null && hand.TryGetValue(wt.defName, out int h)) return "Set by hand to " + (h == 0 ? "off" : h.ToString()) + ". The titles say " + (merged.Get(wt) == 0 ? "off" : merged.Get(wt).ToString()) + ".";
                    var parts = new List<string>();
                    for (int i = 0; i < held.Count; i++) { int pr = held[i].Get(wt); if (pr > 0) parts.Add(held[i].name + ": " + pr + (parts.Count == 0 ? (held.Count > 1 ? "  (used)" : "") : "  (not used: a title above sets this)")); }
                    return parts.Count == 0 ? "None of their titles include this work." : string.Join("\n", parts);
                }, pawn);
            y += 6f;

            // review
            if (merged != null)
            {
                string review = null;
                bool concern = false;
                try { review = ReviewFor(t, pawn, out concern); } catch { }
                if (!review.NullOrEmpty())
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = concern ? new Color(0.95f, 0.60f, 0.25f) : new Color(0.55f, 0.85f, 0.55f);
                    Widgets.Label(new Rect(right.x, y, right.width, 40f), "Review: " + review);
                    GUI.color = Color.white;
                }
            }
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        static void MoveTitle(TimeUseTracker t, Pawn p, int from, int to)
        {
            if (!t.titleOf.TryGetValue(p.thingIDNumber, out List<int> ids)) return;
            List<JobTitle> held = t.TitlesFor(p);
            if (from < 0 || to < 0 || from >= held.Count || to >= held.Count || from == to) return;
            JobTitle moving = held[from];
            held.RemoveAt(from);
            held.Insert(to, moving);
            ids.Clear();
            ids.AddRange(held.Select(x => x.id));
            t.TitlesChanged();
            Reapply(t, p);
        }

        public static void Draw(Rect inRect)
        {
            TimeUseTracker t = TimeUseTracker.Instance;
            if (t == null) return;
            List<Pawn> pawns = JobAdvisor.Pawns;
            List<WorkTypeDef> types = JobAdvisor.Types;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;

            // two views: the titles themselves, or what each colonist holds
            string[] views = { "Titles", "Colonists" };
            for (int i = 0; i < views.Length; i++)
            {
                Rect b = new Rect(inRect.x + i * 118f, inRect.y, 112f, 26f);
                if (mode == i) Widgets.DrawHighlightSelected(b);
                if (Widgets.ButtonText(b, views[i])) { mode = i; dragFrom = -1; }
            }
            inRect = new Rect(inRect.x, inRect.y + 32f, inRect.width, inRect.height - 32f);
            if (mode == 1) { DrawColonists(inRect, t, pawns, types); return; }

            // left: list of titles
            Rect left = new Rect(inRect.x, inRect.y, 230f, inRect.height);
            if (Widgets.ButtonText(new Rect(left.x, left.y, 112f, 28f), "New title"))
            {
                JobTitle nt = new JobTitle { id = t.nextTitleId++, name = "Title " + (t.titles.Count + 1) };
                t.titles.Add(nt);
                selectedId = nt.id;
            }
            if (Widgets.ButtonText(new Rect(left.x + 118f, left.y, 112f, 28f), "Copy colonist"))
            {
                var opts = pawns.OrderBy(p => p.LabelShortCap).Select(p => new FloatMenuOption(p.LabelShortCap + " (" + JobAdvisor.RoleText(p) + ")", () =>
                {
                    JobTitle nt = new JobTitle { id = t.nextTitleId++, name = p.LabelShortCap + "'s job" };
                    foreach (WorkTypeDef wt in types) if (!p.WorkTypeIsDisabled(wt)) { int pr = p.workSettings.GetPriority(wt); if (pr > 0) nt.prio[wt.defName] = pr; }
                    t.titles.Add(nt);
                    selectedId = nt.id;
                })).ToList();
                if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(new Rect(left.x + 118f, left.y, 112f, 28f), "Start a new title from a colonist's current work priorities.");
            HrSettings hr = HumanResourcesMod.Hr;
            Rect libRect = new Rect(left.x, left.y + 32f, 230f, 28f);
            if (hr != null && Widgets.ButtonText(libRect, "Load from library (" + hr.titleLibrary.Count + ")"))
            {
                var opts = new List<FloatMenuOption>();
                foreach (JobTitle lib in hr.titleLibrary.OrderBy(x => x.name))
                {
                    JobTitle src = lib;
                    opts.Add(new FloatMenuOption("Load: " + src.name, () => LoadFromLibrary(t, src)));
                }
                foreach (JobTitle lib in hr.titleLibrary.OrderBy(x => x.name))
                {
                    JobTitle src = lib;
                    opts.Add(new FloatMenuOption("Delete from library: " + src.name, () =>
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Delete \"" + src.name + "\" from the library? Titles already loaded into saved games are not affected.",
                            () => { hr.titleLibrary.Remove(src); WriteLibrary(); }, true))));
                }
                if (opts.Count == 0) opts.Add(new FloatMenuOption("The library is empty. Open a title and press \"Save to library\".", null));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(libRect, "Job titles you saved to the library are available in every game.");
            Rect listOut = new Rect(left.x, left.y + 66f, left.width, left.height - 66f);
            Rect listView = new Rect(0f, 0f, listOut.width - 16f, t.titles.Count * 30f);
            Widgets.BeginScrollView(listOut, ref listScroll, listView);
            for (int i = 0; i < t.titles.Count; i++)
            {
                JobTitle jt = t.titles[i];
                Rect r = new Rect(0f, i * 30f, listView.width, 28f);
                if (jt.id == selectedId) Widgets.DrawHighlightSelected(r);
                else Widgets.DrawHighlightIfMouseover(r);
                int n = t.titleOf.Count(kv => kv.Value.Contains(jt.id));
                Widgets.Label(new Rect(6f, r.y, r.width - 40f, 28f), jt.name);
                GUI.color = Color.gray;
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(r.width - 40f, r.y, 34f, 28f), n.ToString());
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(r)) selectedId = jt.id;
            }
            Widgets.EndScrollView();

            JobTitle title = t.titles.FirstOrDefault(x => x.id == selectedId);
            Rect right = new Rect(left.xMax + 14f, inRect.y, inRect.width - left.width - 14f, inRect.height);
            if (title == null)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(right.x, right.y, right.width, 90f),
                    "A job title is your own template of work priorities, like \"Tailor\" or \"Hauler\".\nCreate one, set its priorities, and assign it to colonists: they are set to it and the advisor leaves them on it.\nPick a title on the left, or make a new one.");
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            List<Pawn> holders = Holders(t, title, pawns);
            var fits = pawns.ToDictionary(p => p, p => Fit(title, p));
            bool skilledTitle = fits.Count > 0 && fits.Values.First() >= 0f;
            // best candidates who don't hold this title: best fit for a skilled title, lowest worker quality for an unskilled one
            List<Pawn> others = pawns.Where(p => !holders.Contains(p)).ToList();
            List<Pawn> ranked = skilledTitle ? others.OrderByDescending(p => fits[p]).ToList() : others.OrderBy(p => JobAdvisor.Quality(p)).ToList();
            Pawn bestOther = ranked.FirstOrDefault();

            // name + delete (fixed above the scrolling part)
            float y = right.y;
            Widgets.Label(new Rect(right.x, y, 50f, 28f), "Name");
            string newName = Widgets.TextField(new Rect(right.x + 54f, y, 280f, 28f), title.name ?? "");
            if (newName != title.name) { title.name = newName; t.TitlesChanged(); }
            if (Widgets.ButtonText(new Rect(right.xMax - 110f, y, 110f, 28f), "Delete title"))
            {
                List<Pawn> had = Holders(t, title, pawns);
                t.ForgetTitle(title);
                t.titles.Remove(title);
                foreach (Pawn p in had) Reapply(t, p);
                selectedId = -1;
                JobAdvisor.Invalidate();
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }
            Rect copyRect = new Rect(right.xMax - 226f, y, 110f, 28f);
            if (Widgets.ButtonText(copyRect, "Copy title"))
            {
                JobTitle nt = new JobTitle { id = t.nextTitleId++, name = (title.name ?? "Title") + " (copy)" };
                foreach (var kv in title.prio) nt.prio[kv.Key] = kv.Value;
                t.titles.Add(nt);
                selectedId = nt.id;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }
            TooltipHandler.TipRegion(copyRect, "Make a new title with the same priorities. Nobody is assigned to the copy.");
            Rect saveRect = new Rect(right.xMax - 362f, y, 130f, 28f);
            if (hr != null && Widgets.ButtonText(saveRect, "Save to library"))
            {
                JobTitle same = hr.titleLibrary.FirstOrDefault(x => x.name == title.name);
                if (same == null) SaveToLibrary(title, null);
                else Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("The library already has a title named \"" + title.name + "\". Replace it?", () => SaveToLibrary(title, same)));
            }
            TooltipHandler.TipRegion(saveRect, "Store this title's name and priorities in the library so it can be loaded into any other game. Who holds it is not stored.");
            y += 36f;

            // priorities: one row in the Work tab's order, with its boxes and number colours. Wraps into more rows only if the screen is too narrow.
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(right.x, y, right.width, 22f), "PRIORITIES   same order and clicks as the Work tab: left-click steps off, 4, 3, 2, 1; right-click steps the other way. Changes apply to everyone holding this title.");
            GUI.color = Color.white;
            y += 24f;
            bool changed = false;
            y += DrawStrip(right.x, y, right.width, types, wt => title.Get(wt), null, null, null,
                (wt, pr, button) =>
                {
                    int next = button == 1 ? (pr + 1) % 5 : (pr + 4) % 5;      // same as the Work tab: left-click raises the priority, right-click lowers it
                    if (next == 0) title.prio.Remove(wt.defName); else title.prio[wt.defName] = next;
                    changed = true;
                },
                wt => JobAdvisor.IsFixed(wt) ? "Leaving this off keeps each colonist's own setting for it." : null);
            if (changed) { t.TitlesChanged(); foreach (Pawn p in holders) Reapply(t, p); JobAdvisor.Invalidate(); }
            y += 10f;

            // everything below scrolls
            Rect lower = new Rect(right.x, y, right.width, Mathf.Max(60f, right.yMax - y));
            float viewH = 30f + Mathf.Max(1, holders.Count) * 44f + 20f + 30f + 3 * 28f + 10f;
            Rect view = new Rect(0f, 0f, lower.width - 16f, viewH);
            Widgets.BeginScrollView(lower, ref bodyScroll, view);
            y = 0f;

            // holders
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, y, 300f, 28f), "Holds this title (" + holders.Count + ")");
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(300f, y, view.width - 460f, 28f), "A colonist can hold up to 5 titles, all used as written. Where two set the same kind of work, the one higher in their list wins.");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(new Rect(view.width - 150f, y, 150f, 28f), "Assign colonist..."))
            {
                var opts = (skilledTitle ? pawns.OrderByDescending(p => fits[p]) : pawns.OrderBy(p => JobAdvisor.Quality(p)))
                    .Where(p => !holders.Contains(p))
                    .Select(p =>
                    {
                        JobTitle cur = t.TitleFor(p);
                        string label = p.LabelShortCap + (skilledTitle ? "  fit " + fits[p].ToString("0.0") : "  quality " + JobAdvisor.Quality(p).ToString("0"))
                            + (cur != null ? "  (adds to: " + cur.name + ")" : "");
                        return new FloatMenuOption(label, () => Add(t, title, p));
                    }).ToList();
                if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
            }
            y += 30f;
            if (holders.Count == 0)
            {
                GUI.color = Color.gray;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(0f, y, view.width, 40f), "Nobody yet.");
                GUI.color = Color.white;
                y += 44f;
            }
            foreach (Pawn p in holders)
            {
                string review = Review(t, title, p, fits[p], bestOther, bestOther != null ? fits[bestOther] : 0f, out bool concern);
                Text.Font = GameFont.Small;
                Rect nameRect = new Rect(0f, y, 150f, 40f);
                Widgets.Label(nameRect, p.LabelShortCap);
                Widgets.DrawHighlightIfMouseover(nameRect);
                if (Widgets.ButtonInvisible(nameRect)) CameraJumper.TryJumpAndSelect(p);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(150f, y, 70f, 40f), skilledTitle ? "fit " + fits[p].ToString("0.0") : "quality " + JobAdvisor.Quality(p).ToString("0"));
                GUI.color = concern ? new Color(0.95f, 0.60f, 0.25f) : new Color(0.55f, 0.85f, 0.55f);
                List<JobTitle> theirs = t.TitlesFor(p);
                int pos = theirs.IndexOf(title);
                string order = theirs.Count > 1 ? "Title " + (pos + 1) + " of " + theirs.Count + " (" + string.Join(" + ", theirs.Select(x => x.name)) + "). " : "";
                Widgets.Label(new Rect(224f, y, view.width - 224f - 200f, 40f), order + review);
                GUI.color = Color.white;
                if (pos > 0)
                {
                    Rect mf = new Rect(view.width - 190f, y + 6f, 92f, 26f);
                    if (Widgets.ButtonText(mf, "Make first")) MakeFirst(t, title, p);
                    TooltipHandler.TipRegion(mf, "Put this title at the top of their list, so it wins wherever it and their other titles set the same kind of work.");
                }
                if (Widgets.ButtonText(new Rect(view.width - 92f, y + 6f, 92f, 26f), "Remove")) Remove(t, title, p);
                y += 44f;
            }
            y += 20f;

            // suggestions
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, y, view.width, 28f), skilledTitle ? "Best suited for this title" : "Suggested for this unskilled title (lowest worker quality first)");
            y += 30f;
            foreach (Pawn p in ranked.Take(3))
            {
                JobTitle cur = t.TitleFor(p);
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(0f, y, 150f, 26f), p.LabelShortCap);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(150f, y, view.width - 150f - 100f, 26f),
                    (skilledTitle ? "fit " + fits[p].ToString("0.0") : "quality " + JobAdvisor.Quality(p).ToString("0")) + "   advisor role: " + JobAdvisor.RoleText(p) + (cur != null ? "   holds: " + cur.name : ""));
                if (Widgets.ButtonText(new Rect(view.width - 92f, y, 92f, 24f), cur != null ? "Add" : "Assign")) Add(t, title, p);
                y += 28f;
            }
            if (ranked.Count == 0)
            {
                GUI.color = Color.gray;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(0f, y, view.width, 26f), "Everyone already holds this title.");
                GUI.color = Color.white;
            }

            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
