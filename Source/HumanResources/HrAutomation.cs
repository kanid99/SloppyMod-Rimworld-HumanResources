using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace SloppyHR
{
    // the automatic side of the HR tab. Both features are off until ticked on the Job advisor page.
    //  - Rest unused work: switch off work types with no work and no time spent for 3 days; switch back when work appears.
    //  - Daily review: once a day, recalculate and apply the advisor's suggestions and report what changed.
    public static class HrAutomation
    {
        const int RestAfterHours = 72;
        static readonly HashSet<string> NeverRest = new HashSet<string>
            { "Firefighter", "Patient", "PatientBedRest", "Doctor", "FSFNurse", "FSFSurgeon", "FSFRescue", "FSFSelfTend", "Childcare", "Warden", "BasicWorker" };

        static string Name(string wt) => DefDatabase<WorkTypeDef>.GetNamedSilentFail(wt)?.labelShort.CapitalizeFirst() ?? wt;

        public static void Hourly(TimeUseTracker t)
        {
            int now = Find.TickManager.TicksGame;
            int hour = now / GenDate.TicksPerHour;

            if (t.resting.Count > 0)
            {
                t.backlog.TryGetValue(hour, out var latest);
                foreach (string wt in t.resting.Keys.ToList())
                    if (!t.restUnused || (latest != null && latest.TryGetValue(wt, out int n) && n > 0)) Wake(t, wt, true);
            }
            if (t.restUnused && hour % 6 == 0) RestCheck(t, hour);

            if (t.autoAssign)
            {
                int day = now / GenDate.TicksPerDay;
                if (t.lastReviewDay < 0) t.lastReviewDay = day;
                else if (day > t.lastReviewDay) { t.lastReviewDay = day; DailyReview(); }
            }
        }

        static void RestCheck(TimeUseTracker t, int hour)
        {
            var window = t.backlog.Where(kv => kv.Key > hour - RestAfterHours).Select(kv => kv.Value).ToList();
            if (window.Count < RestAfterHours - 12) return;          // not enough history yet
            foreach (string wt in Workload.Countable.ToList())
            {
                if (NeverRest.Contains(wt) || t.IsResting(wt)) continue;
                if (window.Any(d => d.TryGetValue(wt, out int n) && n > 0)) continue;
                if (t.ColonyTicks("wt:" + wt, RestAfterHours) > 0) continue;
                WorkTypeDef def = DefDatabase<WorkTypeDef>.GetNamedSilentFail(wt);
                if (def == null) continue;
                var saved = new Dictionary<int, int>();
                foreach (Pawn p in JobAdvisor.Colonists())
                {
                    if (p.WorkTypeIsDisabled(def) || t.TitleFor(p) != null) continue;      // title holders are left alone
                    int pr = p.workSettings.GetPriority(def);
                    if (pr <= 0) continue;
                    saved[p.thingIDNumber] = pr;
                    p.workSettings.SetPriorityQuiet(def, 0);
                }
                if (saved.Count == 0) continue;
                t.resting[wt] = saved;
                Messages.Message("HR: " + Name(wt) + " has had nothing to do for 3 days, so it was switched off for " + saved.Count + " colonist" + (saved.Count == 1 ? "" : "s") + ". It comes back when work appears.", MessageTypeDefOf.SilentInput, false);
            }
        }

        static void Wake(TimeUseTracker t, string wt, bool announce)
        {
            if (!t.resting.TryGetValue(wt, out var saved)) return;
            t.resting.Remove(wt);
            WorkTypeDef def = DefDatabase<WorkTypeDef>.GetNamedSilentFail(wt);
            if (def == null) return;
            int n = 0;
            foreach (Pawn p in JobAdvisor.Colonists())
                if (saved.TryGetValue(p.thingIDNumber, out int pr) && !p.WorkTypeIsDisabled(def) && p.workSettings.GetPriority(def) == 0)
                { p.workSettings.SetPriorityQuiet(def, pr); n++; }
            if (announce && n > 0) Messages.Message("HR: " + Name(wt) + " work appeared, so it was switched back on for " + n + " colonist" + (n == 1 ? "" : "s") + ".", MessageTypeDefOf.SilentInput, false);
        }

        public static void WakeAll(TimeUseTracker t)
        {
            foreach (string wt in t.resting.Keys.ToList()) Wake(t, wt, false);
        }

        static void DailyReview()
        {
            List<string> behind = Workload.BehindNames();
            int changes = JobAdvisor.AutoApply(out int colonists, out List<string> examples);
            if (changes == 0) return;
            string text = "The daily HR review changed " + changes + " work priorit" + (changes == 1 ? "y" : "ies") + " for " + colonists + " colonist" + (colonists == 1 ? "" : "s") + ".";
            if (behind.Count > 0) text += "\n\nWork that fell behind and got more people:\n- " + string.Join("\n- ", behind);
            text += "\n\nChanges" + (colonists > examples.Count ? " (first " + examples.Count + " colonists)" : "") + ":\n- " + string.Join("\n- ", examples);
            text += "\n\nTo revert, open the HR tab, Job advisor, and press Undo last apply. Untick Daily review there to stop these.";
            Find.LetterStack.ReceiveLetter("HR daily review", text, LetterDefOf.NeutralEvent);
        }
    }
}
