using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace SloppyHR
{
    // a "Title" column on the normal Work tab showing each colonist's job title. Click it to assign or remove one.
    public class PawnColumnWorker_JobTitle : PawnColumnWorker_Text
    {
        protected override int Width => 110;

        protected override string GetTextFor(Pawn pawn) => TimeUseTracker.Instance?.TitleFor(pawn)?.name ?? "";

        // Clicking the column header opens a menu: give one title to everyone in the table, clear everyone's title, or sort.
        protected override void HeaderClicked(Rect headerRect, PawnTable table)
        {
            TimeUseTracker t = TimeUseTracker.Instance;
            if (t == null) { base.HeaderClicked(headerRect, table); return; }
            List<Pawn> all = table.PawnsListForReading.Where(p => p?.workSettings != null && p.workSettings.EverWork).ToList();
            int titled = all.Count(p => t.TitleFor(p) != null);
            var opts = new List<FloatMenuOption>();
            foreach (JobTitle jt in t.TitlesSorted)
            {
                JobTitle pick = jt;
                opts.Add(new FloatMenuOption("Assign \"" + pick.name + "\" to all (" + all.Count + ")", () =>
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "Give the title \"" + pick.name + "\" to all " + all.Count + " colonists in this list?\n\nEveryone's work priorities are set to the title, and any titles they hold now are replaced.",
                        () => { foreach (Pawn p in all) JobTitles.Assign(t, pick, p); }, true))));
                opts.Add(new FloatMenuOption("Add \"" + pick.name + "\" to all, after the titles they hold", () =>
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "Add the title \"" + pick.name + "\" to all " + all.Count + " colonists in this list, after any titles they already hold?\n\nColonists who already hold " + TimeUseTracker.MaxTitles + " titles are skipped.",
                        () => { foreach (Pawn p in all) if (t.AddTitle(p, pick)) JobTitles.Reapply(t, p); }, true))));
            }
            if (t.titles.Count == 0) opts.Add(new FloatMenuOption("No job titles yet. Make one on the HR tab, Job titles page.", null));
            if (titled > 0)
                opts.Add(new FloatMenuOption("Remove title from all (" + titled + ")", () =>
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "Remove the job title from all " + titled + " colonists who hold one?\n\nTheir work priorities stay as they are, and the HR advisor may change them again.",
                        () => { foreach (Pawn p in all) JobTitles.ClearAll(t, p); }, true))));
            opts.Add(new FloatMenuOption("Sort by title (A to Z)", () => table.SortBy(def, false)));
            opts.Add(new FloatMenuOption("Sort by title (Z to A)", () => table.SortBy(def, true)));
            if (table.SortingBy == def) opts.Add(new FloatMenuOption("Stop sorting by title", () => table.SortBy(null, false)));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        public override void DoCell(Rect rect, Pawn pawn, PawnTable table)
        {
            TimeUseTracker t = TimeUseTracker.Instance;
            if (t == null || pawn?.workSettings == null || !pawn.workSettings.EverWork) return;
            JobTitle title = t.TitleFor(pawn);
            Rect r = new Rect(rect.x + 2f, rect.y, rect.width - 4f, Mathf.Min(rect.height, 30f));
            string review = null;
            bool concern = false;
            if (title != null) try { review = JobTitles.ReviewFor(t, pawn, out concern); } catch { }

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            GUI.color = title == null ? new Color(1f, 1f, 1f, 0.3f) : concern ? new Color(0.95f, 0.60f, 0.25f) : Color.white;
            int handSet = title == null ? 0 : t.OverrideCount(pawn);
            Widgets.Label(r, title == null ? "-" : ((handSet > 0 ? "* " : "") + title.name).Truncate(r.width));
            GUI.color = Color.white;
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;

            if (!Mouse.IsOver(r)) return;
            Widgets.DrawHighlight(r);
            TooltipHandler.TipRegion(r, title == null
                ? "No job title. Click to give this colonist one."
                : "Job title: " + title.name + (review.NullOrEmpty() ? "" : "\n\n" + review) + (handSet > 0 ? "\n\n* " + handSet + " box" + (handSet == 1 ? "" : "es") + " set by hand on top of the title" + (t.TitleCount(pawn) > 1 ? "s" : "") + ". They are kept when titles change; clear them on the HR tab, Job titles, Colonists." : "") + "\n\nThe HR advisor leaves this colonist's priorities alone while they hold a title. Click to add, reorder or remove titles (up to 5; the first is the main job).");
            if (Widgets.ButtonInvisible(r))
            {
                var opts = new List<FloatMenuOption>();
                List<JobTitle> held = t.TitlesFor(pawn);
                int own = t.OwnTitlesFor(pawn).Count;       // the default title is extra and always last
                foreach (JobTitle jt in t.TitlesSorted)
                {
                    JobTitle pick = jt;
                    if (held.Contains(pick)) continue;
                    if (t.IsDefault(pick)) opts.Add(new FloatMenuOption("Use the default title: " + pick.name, () => JobTitles.Add(t, pick, pawn)));
                    else if (held.Count == 0) opts.Add(new FloatMenuOption("Give title: " + pick.name, () => JobTitles.Assign(t, pick, pawn)));
                    else if (own < TimeUseTracker.MaxTitles) opts.Add(new FloatMenuOption("Add as title " + (own + 1) + ": " + pick.name, () => JobTitles.Add(t, pick, pawn)));
                }
                if (held.Count > 0)
                    foreach (JobTitle jt in t.TitlesSorted)
                    {
                        JobTitle pick = jt;
                        if (t.IsDefault(pick) || (own == 1 && held[0] == pick)) continue;
                        opts.Add(new FloatMenuOption("Replace with only: " + pick.name, () => JobTitles.Assign(t, pick, pawn)));
                    }
                for (int i = 1; i < own; i++) { JobTitle pick = held[i]; opts.Add(new FloatMenuOption("Make first: " + pick.name, () => JobTitles.MakeFirst(t, pick, pawn))); }
                if (held.Count > 1) foreach (JobTitle jt in held) { JobTitle pick = jt; opts.Add(new FloatMenuOption((t.IsDefault(pick) ? "Switch off the default: " : "Remove: ") + pick.name, () => JobTitles.Remove(t, pick, pawn))); }
                if (held.Count > 0) opts.Add(new FloatMenuOption(held.Count > 1 ? "Remove all titles (priorities stay as they are)" : "Remove title (priorities stay as they are)", () => JobTitles.ClearAll(t, pawn)));
                if (opts.Count == 0) opts.Add(new FloatMenuOption("No job titles yet. Make one on the HR tab, Job titles page.", null));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class WorkTabTitleColumn
    {
        static WorkTabTitleColumn()
        {
            try
            {
                PawnColumnDef col = DefDatabase<PawnColumnDef>.GetNamedSilentFail("SloppyHR_JobTitle");
                PawnTableDef work = PawnTableDefOf.Work;
                if (col == null || work?.columns == null || work.columns.Contains(col)) return;
                // right after the name column
                int at = work.columns.FindIndex(c => c.workerClass != null && typeof(PawnColumnWorker_Label).IsAssignableFrom(c.workerClass));
                work.columns.Insert(at < 0 ? Math.Min(1, work.columns.Count) : at + 1, col);
            }
            catch (Exception e) { Log.Warning(HR.Tag + "could not add the job title column to the Work tab: " + e.Message); }
        }
    }
}
