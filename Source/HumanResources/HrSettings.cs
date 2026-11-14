using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace SloppyHR
{
    // mod options for the HR tab. For now: which workbenches and bills are left out of the workload analysis.
    public class HrSettings : ModSettings
    {
        public HashSet<string> excludedBenches = new HashSet<string>();     // ThingDef names
        public HashSet<string> excludedRecipes = new HashSet<string>();     // RecipeDef names
        public bool importedPrototype;                                    // the prototype's settings were looked for once
        public List<JobTitle> titleLibrary = new List<JobTitle>();        // job titles shared between all saves
        List<string> benchList, recipeList;

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) { benchList = excludedBenches.ToList(); recipeList = excludedRecipes.ToList(); }
            Scribe_Collections.Look(ref benchList, "excludedBenches", LookMode.Value);
            Scribe_Collections.Look(ref recipeList, "excludedRecipes", LookMode.Value);
            Scribe_Collections.Look(ref titleLibrary, "titleLibrary", LookMode.Deep);
            Scribe_Values.Look(ref importedPrototype, "importedPrototype");
            if (Scribe.mode == LoadSaveMode.PostLoadInit || Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (titleLibrary == null) titleLibrary = new List<JobTitle>();
                titleLibrary.RemoveAll(x => x == null);
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                excludedBenches = benchList != null ? new HashSet<string>(benchList) : new HashSet<string>();
                excludedRecipes = recipeList != null ? new HashSet<string>(recipeList) : new HashSet<string>();
            }
        }
    }

    public static class HrSettingsUI
    {
        static Vector2 benchScroll, recipeScroll;
        static string filter = "";

        public static void Draw(Rect inRect)
        {
            HrSettings hr = HumanResourcesMod.Hr;
            if (hr == null) { Widgets.Label(inRect, "Settings could not be loaded."); return; }
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 44f),
                "Workload analysis: untick a workbench or a bill to leave it out. Use this for standing bills that only have work now and then (recycling, for example), so they aren't reported as stuck and don't draw extra workers.");
            Widgets.Label(new Rect(inRect.x, inRect.y + 48f, 50f, 26f), "Filter");
            filter = Widgets.TextField(new Rect(inRect.x + 54f, inRect.y + 48f, 240f, 26f), filter ?? "");
            string f = filter.Trim().ToLowerInvariant();
            bool changed = false;

            float top = inRect.y + 84f, colW = (inRect.width - 16f) / 2f, h = inRect.yMax - top;

            // workbenches
            Rect bl = new Rect(inRect.x, top, colW, h);
            Widgets.Label(new Rect(bl.x, bl.y, bl.width, 24f), "Workbenches (ticked = counted)");
            List<ThingDef> benches = Workload.EnsureBillTypes().Keys.Where(d => f.Length == 0 || d.label.ToLowerInvariant().Contains(f))
                .OrderByDescending(d => hr.excludedBenches.Contains(d.defName)).ThenBy(d => d.label).ToList();
            Rect bOut = new Rect(bl.x, bl.y + 28f, bl.width, bl.height - 28f);
            Rect bView = new Rect(0f, 0f, bOut.width - 16f, benches.Count * 26f);
            Widgets.BeginScrollView(bOut, ref benchScroll, bView);
            for (int i = 0; i < benches.Count; i++)
            {
                float ry = i * 26f;
                if (ry + 26f < benchScroll.y || ry > benchScroll.y + bOut.height) continue;
                ThingDef d = benches[i];
                bool counted = !hr.excludedBenches.Contains(d.defName), was = counted;
                Widgets.CheckboxLabeled(new Rect(0f, ry, bView.width, 24f), d.LabelCap + "  (" + Workload.EnsureBillTypes()[d].labelShort + ")", ref counted);
                if (counted != was) { if (counted) hr.excludedBenches.Remove(d.defName); else hr.excludedBenches.Add(d.defName); changed = true; }
            }
            Widgets.EndScrollView();

            // bills: the ones open in the colony right now, plus everything already left out
            Rect rl = new Rect(inRect.x + colW + 16f, top, colW, h);
            Widgets.Label(new Rect(rl.x, rl.y, rl.width, 24f), "Bills (ticked = counted)");
            var recipes = new Dictionary<string, RecipeDef>();
            foreach (string name in hr.excludedRecipes) { RecipeDef r = DefDatabase<RecipeDef>.GetNamedSilentFail(name); if (r != null) recipes[name] = r; }
            if (Current.ProgramState == ProgramState.Playing)
                foreach (Map map in Find.Maps)
                    if (map.IsPlayerHome)
                        foreach (Building b in map.listerBuildings.allBuildingsColonist)
                            if (b is IBillGiver g && g.BillStack != null)
                                foreach (Bill bill in g.BillStack.Bills) if (bill.recipe != null) recipes[bill.recipe.defName] = bill.recipe;
            List<RecipeDef> list = recipes.Values.Where(r => f.Length == 0 || r.label.ToLowerInvariant().Contains(f))
                .OrderByDescending(r => hr.excludedRecipes.Contains(r.defName)).ThenBy(r => r.label).ToList();
            Rect rOut = new Rect(rl.x, rl.y + 28f, rl.width, rl.height - 28f);
            Rect rView = new Rect(0f, 0f, rOut.width - 16f, Mathf.Max(1, list.Count) * 26f);
            Widgets.BeginScrollView(rOut, ref recipeScroll, rView);
            if (list.Count == 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(0f, 0f, rView.width, 52f), Current.ProgramState == ProgramState.Playing ? "No bills found." : "Load a save to see the bills in your colony.");
                GUI.color = Color.white;
            }
            for (int i = 0; i < list.Count; i++)
            {
                float ry = i * 26f;
                if (ry + 26f < recipeScroll.y || ry > recipeScroll.y + rOut.height) continue;
                RecipeDef r = list[i];
                bool counted = !hr.excludedRecipes.Contains(r.defName), was = counted;
                Widgets.CheckboxLabeled(new Rect(0f, ry, rView.width, 24f), r.LabelCap, ref counted);
                if (counted != was) { if (counted) hr.excludedRecipes.Remove(r.defName); else hr.excludedRecipes.Add(r.defName); changed = true; }
            }
            Widgets.EndScrollView();

            if (changed) Workload.AfterExclusionChange();
        }
    }
}
