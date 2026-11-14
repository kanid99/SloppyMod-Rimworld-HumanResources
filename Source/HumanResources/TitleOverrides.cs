using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace SloppyHR
{
    // when the player changes a title holder's work box by hand (on the Work tab or anywhere else in the
    // UI), remember it as a personal override so it survives the next time their titles are re-applied.
    [StaticConstructorOnStartup]
    public static class TitleOverrides
    {
        public static bool Patched;

        static TitleOverrides()
        {
            try
            {
                MethodInfo set = AccessTools.Method(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority), new[] { typeof(WorkTypeDef), typeof(int) });
                if (set == null) { Log.Warning(HR.Tag + "Hand edits to title holders will not be remembered: SetPriority not found"); return; }
                new Harmony(HR.HarmonyId).Patch(set, postfix: new HarmonyMethod(typeof(TitleOverrides), nameof(Postfix)));
                Patched = true;
            }
            catch (Exception e) { Log.Warning(HR.Tag + "Hand edits to title holders will not be remembered: " + HR.Reason(e)); }
        }

        static void Postfix(Pawn ___pawn, WorkTypeDef w, int priority)
        {
            try
            {
                // only changes made from the interface count: the game and other mods also set priorities while it runs
                if (JobTitles.Applying || ___pawn == null || w == null || Event.current == null || Current.ProgramState != ProgramState.Playing) return;
                JobTitles.NoteHandEdit(___pawn, w, priority);
            }
            catch (Exception ex) { Log.ErrorOnce(HR.Tag + "Recording a hand edit failed: " + ex, 81730072); }
        }
    }
}

namespace SloppyHR
{
    public static class WorkSettingsQuiet
    {
        // A priority written by the HR mod itself (advisor, undo, resting work): never counted as a hand edit.
        public static void SetPriorityQuiet(this Pawn_WorkSettings ws, WorkTypeDef wt, int priority)
        {
            bool was = JobTitles.Applying;
            JobTitles.Applying = true;
            try { ws.SetPriority(wt, priority); }
            finally { JobTitles.Applying = was; }
        }
    }
}
