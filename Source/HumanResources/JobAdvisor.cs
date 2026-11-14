using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace SloppyHR
{
    // "Job advisor" page of the Time use window. Scores every colonist for every work type, suggests work
    // priorities, flags colonists who are best used as support (hauling, cleaning and other unskilled work), and can
    // apply the suggestions with one level of undo.
    public static class JobAdvisor
    {
        class Cell
        {
            public WorkTypeDef wt;
            public bool disabled, skilled, fixedType, kept, resting, rotation;
            public int cur, sug, rank, capable, ownRank;
            public float score, skill, passionBonus, learnFactor, health, bonus = 1f;
            public string bonusFrom, expertiseFrom, passionLabel, reservedFor;
            public bool backup, promoted, reassigned;
            public string title;
            public float expertise = 1f;
            public bool role;
        }

        class PRow
        {
            public Pawn pawn;
            public Cell[] cells;
            public bool support, generalist;
            public string role, roleTip;
            public int changes;
            public float quality;
            public string qualityTip;
            public string titleName, review;      // job title held, and the HR review of how that is going
            public bool reviewConcern, reviewDone;
        }

        // Work that lists a skill but doesn't train or use it: Complex Jobs' Nurse is feeding, carrying and visiting
        // patients, none of which is Medicine work. It is treated as unskilled, and kept off colonists with a medical role.
        static readonly HashSet<string> CareTypes = new HashSet<string> { "FSFNurse" };
        static readonly HashSet<string> MedicalRoleTypes = new HashSet<string> { "Doctor", "FSFSurgeon" };
        static bool NoSkill(WorkTypeDef wt) => wt.relevantSkills.NullOrEmpty() || CareTypes.Contains(wt.defName) || BenchNoSkill().Contains(wt.defName);

        // Bench-only work types that list a skill although none of their recipes uses or trains one (refining chemfuel,
        // for example). Worked out from the recipes actually in the game, so other mods' benches are counted too.
        static HashSet<string> benchNoSkill;
        static HashSet<string> BenchNoSkill()
        {
            if (benchNoSkill != null) return benchNoSkill;
            var result = new HashSet<string>();
            try
            {
                var benches = new Dictionary<WorkTypeDef, List<ThingDef>>();
                var mixed = new HashSet<WorkTypeDef>();     // work types with something other than bench bills in them
                foreach (WorkGiverDef wg in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                {
                    if (wg.workType == null) continue;
                    if (wg.fixedBillGiverDefs.NullOrEmpty()) { mixed.Add(wg.workType); continue; }
                    if (!benches.TryGetValue(wg.workType, out List<ThingDef> list)) benches[wg.workType] = list = new List<ThingDef>();
                    list.AddRange(wg.fixedBillGiverDefs.Where(d => d != null));
                }
                foreach (var kv in benches)
                {
                    if (mixed.Contains(kv.Key) || kv.Key.relevantSkills.NullOrEmpty()) continue;
                    bool anySkilled = false, anyRecipe = false;
                    foreach (ThingDef bench in kv.Value)
                    {
                        List<RecipeDef> recipes = null;
                        try { recipes = bench.AllRecipes; } catch { }
                        if (recipes == null) continue;
                        foreach (RecipeDef r in recipes)
                        {
                            anyRecipe = true;
                            if ((r.workSkill != null && r.workSkillLearnFactor > 0f) || !r.skillRequirements.NullOrEmpty()) { anySkilled = true; break; }
                        }
                        if (anySkilled) break;
                    }
                    if (anyRecipe && !anySkilled) result.Add(kv.Key.defName);
                }
                if (result.Count > 0) Log.Message(HR.Tag + "Treated as unskilled because none of their recipes use a skill: " + string.Join(", ", result));
            }
            catch (Exception e) { Log.Warning(HR.Tag + "could not check bench recipes for skill use: " + e.Message); }
            return benchNoSkill = result;
        }

        // "Medicine 7 (burning)" for each skill a kind of work uses; null for unskilled work.
        public static string SkillText(Pawn p, WorkTypeDef wt)
        {
            if (p?.skills == null || wt.relevantSkills.NullOrEmpty()) return null;
            var parts = new List<string>();
            foreach (SkillDef sd in wt.relevantSkills)
            {
                SkillRecord rec = p.skills.GetSkill(sd);
                if (rec == null) continue;
                parts.Add(sd.LabelCap + " " + (rec.TotallyDisabled ? "-" : rec.Level.ToString()) + (rec.passion != Passion.None ? " (" + PassionLabel(rec.passion) + " passion)" : ""));
            }
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        static readonly HashSet<string> FixedTypes = new HashSet<string> { "Firefighter", "Patient", "PatientBedRest", "FSFRescue", "FSFSelfTend" };
        static List<WorkTypeDef> types = new List<WorkTypeDef>();
        static List<PRow> rows = new List<PRow>();
        static readonly HashSet<string> keptFallback = new HashSet<string>();
        static HashSet<string> kept => TimeUseTracker.Instance?.kept ?? keptFallback;
        static Dictionary<Pawn, Dictionary<WorkTypeDef, int>> undo;
        static int stamp = -1;
        static Vector2 scroll;
        static bool sortByQuality;
        static readonly Dictionary<WorkTypeDef, float> dayHours = new Dictionary<WorkTypeDef, float>();   // colonist-hours spent on a type in the last day
        static readonly Dictionary<WorkTypeDef, int> slots = new Dictionary<WorkTypeDef, int>();         // how many priority-1 workers that justifies
        const float HoursPerWorker = 8f;

        static string Key(Pawn p, WorkTypeDef wt) => p.thingIDNumber + "|" + wt.defName;

        public static IEnumerable<Pawn> Colonists()
        {
            Faction player = Faction.OfPlayerSilentFail;
            if (player == null) yield break;
            foreach (Map map in Find.Maps)
                foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(player))
                    if (p != null && !p.Dead && p.RaceProps.Humanlike && p.workSettings != null && p.workSettings.EverWork && p.skills != null) yield return p;
        }

        // Used by the daily review: recalculate, apply everything, and describe what changed.
        public static int AutoApply(out int colonists, out List<string> examples)
        {
            Rebuild();
            stamp = Find.TickManager.TicksGame / 250;
            examples = new List<string>();
            int changes = 0; colonists = 0;
            foreach (PRow row in rows)
            {
                if (row.changes == 0) continue;
                colonists++;
                changes += row.changes;
                if (examples.Count < 10)
                    examples.Add(row.pawn.LabelShortCap + ": " + string.Join(", ", row.cells.Where(c => !c.disabled && c.sug != c.cur).Take(4)
                        .Select(c => c.wt.labelShort + " " + (c.cur == 0 ? "off" : c.cur.ToString()) + ">" + (c.sug == 0 ? "off" : c.sug.ToString()))) + (row.changes > 4 ? ", ..." : ""));
            }
            if (changes > 0) Apply(rows.ToList());
            return changes;
        }

        // ---- role templates: which stats matter for each work type ----

        static readonly Dictionary<string, string[]> KnownStats = new Dictionary<string, string[]>
        {
            { "Doctor", new[] { "MedicalTendQuality", "MedicalTendSpeed", "MedicalSurgerySuccessChance" } },
            { "Warden", new[] { "NegotiationAbility" } },
            { "Handling", new[] { "TameAnimalChance", "TrainAnimalChance", "AnimalGatherYield" } },
            { "Cooking", new[] { "CookSpeed", "ButcheryFleshSpeed", "ButcheryFleshEfficiency" } },
            { "Hunting", new[] { "ShootingAccuracyPawn", "HuntingStealth" } },
            { "Construction", new[] { "ConstructionSpeed", "ConstructSuccessChance" } },
            { "Growing", new[] { "PlantWorkSpeed", "PlantHarvestYield" } },
            { "PlantCutting", new[] { "PlantWorkSpeed" } },
            { "Mining", new[] { "MiningSpeed", "MiningYield" } },
            { "Research", new[] { "ResearchSpeed" } },
            { "Smithing", new[] { "GeneralLaborSpeed" } },
            { "Tailoring", new[] { "GeneralLaborSpeed" } },
            { "Art", new[] { "GeneralLaborSpeed" } },
            { "Crafting", new[] { "GeneralLaborSpeed" } },
            // [FSF] Complex Jobs
            { "FSFNurse", new[] { "MedicalTendQuality", "MedicalTendSpeed" } },
            { "FSFSurgeon", new[] { "MedicalSurgerySuccessChance", "MedicalOperationSpeed" } },
            { "FSFTraining", new[] { "TrainAnimalChance" } },
            { "FSFButcher", new[] { "ButcheryFleshSpeed", "ButcheryFleshEfficiency" } },
            { "FSFRepair", new[] { "ConstructionSpeed", "FixBrokenDownBuildingSuccessChance" } },
            { "FSFDeconstruct", new[] { "ConstructionSpeed" } },
            { "FSFHarvesting", new[] { "PlantWorkSpeed", "PlantHarvestYield" } },
            { "FSFDrilling", new[] { "DeepDrillingSpeed", "MiningYield" } },
            { "FSFDrugs", new[] { "DrugSynthesisSpeed", "DrugCookingSpeed" } },
            { "FSFMachining", new[] { "GeneralLaborSpeed" } },
            { "FSFFabrication", new[] { "GeneralLaborSpeed" } },
            { "FSFRefining", new[] { "GeneralLaborSpeed" } },
            { "FSFScan", new[] { "ResearchSpeed" } },
        };

        static Dictionary<WorkTypeDef, List<StatDef>> roleStats;

        // Stats that matter for a work type: a built-in list for the vanilla types, plus whatever speed and efficiency
        // stats the recipes at that work type's benches actually use (this is what covers modded work types).
        static List<StatDef> StatsFor(WorkTypeDef wt)
        {
            if (roleStats == null)
            {
                roleStats = new Dictionary<WorkTypeDef, List<StatDef>>();
                var counts = new Dictionary<WorkTypeDef, Dictionary<StatDef, int>>();
                foreach (var kv in Workload.EnsureBillTypes())
                {
                    if (kv.Key.AllRecipes == null) continue;
                    if (!counts.TryGetValue(kv.Value, out var c)) counts[kv.Value] = c = new Dictionary<StatDef, int>();
                    foreach (RecipeDef r in kv.Key.AllRecipes)
                    {
                        if (r.workSpeedStat != null) { c.TryGetValue(r.workSpeedStat, out int n); c[r.workSpeedStat] = n + 1; }
                        if (r.efficiencyStat != null) { c.TryGetValue(r.efficiencyStat, out int n); c[r.efficiencyStat] = n + 1; }
                    }
                }
                foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                {
                    var list = new List<StatDef>();
                    if (KnownStats.TryGetValue(w.defName, out string[] names))
                        foreach (string n in names) { StatDef sd = DefDatabase<StatDef>.GetNamedSilentFail(n); if (sd != null) list.Add(sd); }
                    if (counts.TryGetValue(w, out var cc))
                        foreach (StatDef sd in cc.OrderByDescending(x => x.Value).Take(3).Select(x => x.Key)) if (!list.Contains(sd)) list.Add(sd);
                    list.Remove(StatDefOf.WorkSpeedGlobal);     // already part of the health / work speed factor
                    roleStats[w] = list;
                }
            }
            return roleStats.TryGetValue(wt, out var res) ? res : new List<StatDef>();
        }

        // What a colonist's traits and genes do to the stats of a work type: 1.0 = nothing, 1.2 = 20% better.
        static float TraitGeneBonus(Pawn p, List<StatDef> stats, out string from)
        {
            from = null;
            if (stats.Count == 0) return 1f;
            var names = new List<string>();
            float sum = 0f;
            foreach (StatDef stat in stats)
            {
                float offset = 0f, factor = 1f;
                void Take(List<StatModifier> offs, List<StatModifier> facs, string label)
                {
                    bool hit = false;
                    if (offs != null) foreach (StatModifier m in offs) if (m.stat == stat && m.value != 0f) { offset += m.value; hit = true; }
                    if (facs != null) foreach (StatModifier m in facs) if (m.stat == stat && m.value != 1f) { factor *= m.value; hit = true; }
                    if (hit && !names.Contains(label)) names.Add(label);
                }
                if (p.story?.traits != null)
                    foreach (Trait t in p.story.traits.allTraits)
                        if (!t.Suppressed && t.CurrentData != null) Take(t.CurrentData.statOffsets, t.CurrentData.statFactors, t.LabelCap);
                if (ModsConfig.BiotechActive && p.genes != null)
                    foreach (Gene g in p.genes.GenesListForReading)
                        if (g.Active) Take(g.def.statOffsets, g.def.statFactors, g.LabelCap);
                sum += Mathf.Max(0f, 1f + offset) * factor;
            }
            if (names.Count > 0) from = string.Join(", ", names);
            return Mathf.Clamp(sum / stats.Count, 0.4f, 2f);
        }

        // ---- Vanilla Skills Expanded (optional): expertise and its own passion types, read by reflection ----

        class ExpertiseInfo { public string label; public int level; public List<StatModifier> offsets, factors; }

        static bool vseChecked;
        static System.Reflection.MethodInfo vseExpertiseOf, vsePassionToDef;
        static Type vseSkillsMod;

        static void VseInit()
        {
            if (vseChecked) return;
            vseChecked = true;
            try
            {
                Type trackers = HarmonyLib.AccessTools.TypeByName("VSE.ExpertiseTrackers");
                if (trackers != null) vseExpertiseOf = HarmonyLib.AccessTools.Method(trackers, "Expertise", new[] { typeof(Pawn) });
                Type pm = HarmonyLib.AccessTools.TypeByName("VSE.Passions.PassionManager");
                if (pm != null) vsePassionToDef = HarmonyLib.AccessTools.Method(pm, "PassionToDef", new[] { typeof(Passion) });
                vseSkillsMod = HarmonyLib.AccessTools.TypeByName("VSE.SkillsMod");
            }
            catch { }
        }

        static float VseStatMultiplier()
        {
            try
            {
                if (vseSkillsMod == null) return 1f;
                var t = HarmonyLib.Traverse.Create(vseSkillsMod);
                object settings = t.Field("Settings").FieldExists() ? t.Field("Settings").GetValue() : t.Property("Settings").GetValue();
                return settings == null ? 1f : HarmonyLib.Traverse.Create(settings).Field("StatMultiplier").GetValue<float>();
            }
            catch { return 1f; }
        }

        static List<ExpertiseInfo> ExpertiseOf(Pawn p)
        {
            VseInit();
            var list = new List<ExpertiseInfo>();
            if (vseExpertiseOf == null) return list;
            try
            {
                object tracker = vseExpertiseOf.Invoke(null, new object[] { p });
                if (tracker == null) return list;
                var all = HarmonyLib.Traverse.Create(tracker).Property("AllExpertise").GetValue<System.Collections.IEnumerable>();
                if (all == null) return list;
                foreach (object rec in all)
                {
                    var tr = HarmonyLib.Traverse.Create(rec);
                    Def def = tr.Field("def").GetValue<Def>();
                    int level = tr.Property("Level").GetValue<int>();
                    if (def == null || level <= 0) continue;
                    var dt = HarmonyLib.Traverse.Create(def);
                    list.Add(new ExpertiseInfo { label = def.LabelCap, level = level, offsets = dt.Field("statOffsets").GetValue<List<StatModifier>>(), factors = dt.Field("statFactors").GetValue<List<StatModifier>>() });
                }
            }
            catch { }
            return list;
        }

        static string PassionLabel(Passion passion)
        {
            VseInit();
            try
            {
                if (vsePassionToDef != null && vsePassionToDef.Invoke(null, new object[] { passion }) is Def d && !d.label.NullOrEmpty()) return d.label;
            }
            catch { }
            return passion == Passion.None ? "none" : passion == Passion.Minor ? "interested" : passion == Passion.Major ? "burning" : passion.ToString();
        }

        // Stats an expertise can boost that mark it as belonging to a role, on top of the role's own stat list.
        static readonly Dictionary<string, string[]> ExpertiseStats = new Dictionary<string, string[]>
        {
            { "Tailoring", new[] { "VSE_TailoringSpeed" } },
            { "Smithing", new[] { "VSE_WeaponCreationSpeed" } },
            { "FSFMachining", new[] { "VSE_WeaponCreationSpeed" } },
            { "Crafting", new[] { "VSE_ComponentCraftingSpeed", "ButcheryMechanoidSpeed", "ButcheryMechanoidEfficiency" } },
            { "FSFFabrication", new[] { "VSE_ComponentCraftingSpeed" } },
            { "Art", new[] { "VSE_ArtSpeed", "VSE_ArtQuality" } },
            { "Construction", new[] { "VSE_ConstructQuality", "VSE_FloorSpeed", "SmoothingSpeed" } },
            { "FSFRepair", new[] { "VSE_RepairSpeed" } },
            { "Cooking", new[] { "FoodPoisonChance", "DrugCookingSpeed" } },
            { "Warden", new[] { "ArrestSuccessChance", "VSE_RecruitRate", "SuppressionPower" } },
            { "Handling", new[] { "AnimalGatherSpeed" } },
            { "Mining", new[] { "VSE_RockChunkChance" } },
        };

        // How much a colonist's expertise helps a role. The strongest matching expertise counts in full (5% per
        // level at default settings), any others at a quarter.
        static float ExpertiseBonus(List<ExpertiseInfo> expertise, WorkTypeDef wt, float statMultiplier, out string from)
        {
            from = null;
            if (expertise.Count == 0) return 1f;
            List<StatDef> stats = StatsFor(wt);
            ExpertiseStats.TryGetValue(wt.defName, out string[] extra);
            bool Matches(StatDef sd) => sd != null && (stats.Contains(sd) || (extra != null && extra.Contains(sd.defName)));
            var effects = new List<float>();
            var names = new List<string>();
            foreach (ExpertiseInfo e in expertise)
            {
                float best = 0f;
                if (e.offsets != null) foreach (StatModifier m in e.offsets) if (Matches(m.stat)) best = Mathf.Max(best, Mathf.Min(Mathf.Abs(m.value), 0.05f));
                if (e.factors != null) foreach (StatModifier m in e.factors) if (Matches(m.stat)) best = Mathf.Max(best, Mathf.Min(Mathf.Abs(m.value), 0.05f));
                if (best <= 0f) continue;
                effects.Add(best * e.level * statMultiplier);
                names.Add(e.label + " " + e.level);
            }
            if (effects.Count == 0) return 1f;
            from = string.Join(", ", names);
            float top = effects.Max();
            return Mathf.Clamp(1f + top + 0.25f * (effects.Sum() - top), 1f, 2.5f);
        }

        static string RoleName(WorkTypeDef wt) => (wt.pawnLabel.NullOrEmpty() ? wt.labelShort : wt.pawnLabel).CapitalizeFirst();

        const float RoleFit = 8f;        // fit needed to hold a role
        const float GeneralistFit = 5f;  // below this for everything = support worker

        static void Rebuild()
        {
            rows.Clear();
            types = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => w.visible).OrderByDescending(w => w.naturalPriority).ToList();
            Faction player = Faction.OfPlayerSilentFail;
            if (player == null) return;
            TimeUseTracker tracker = TimeUseTracker.Instance;

            foreach (Map map in Find.Maps)
                foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(player))
                {
                    if (p == null || p.Dead || !p.RaceProps.Humanlike || p.workSettings == null || !p.workSettings.EverWork || p.skills == null) continue;
                    PRow row = new PRow { pawn = p, cells = new Cell[types.Count] };
                    float health = Mathf.Clamp(p.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation), 0.3f, 1.3f)
                                 * Mathf.Clamp(p.GetStatValue(StatDefOf.WorkSpeedGlobal), 0.3f, 1.5f);
                    List<ExpertiseInfo> expertise = ExpertiseOf(p);
                    row.titleName = tracker?.TitleFor(p)?.name;
                    float vseMult = expertise.Count > 0 ? VseStatMultiplier() : 1f;
                    for (int t = 0; t < types.Count; t++)
                    {
                        WorkTypeDef wt = types[t];
                        Cell c = new Cell { wt = wt, disabled = p.WorkTypeIsDisabled(wt) };
                        c.cur = c.disabled ? 0 : p.workSettings.GetPriority(wt);
                        c.fixedType = FixedTypes.Contains(wt.defName);
                        c.skilled = !c.fixedType && !NoSkill(wt);
                        c.title = row.titleName;
                        c.kept = row.titleName != null || kept.Contains(Key(p, wt));     // title holders are never changed by the advisor
                        if (c.skilled && !c.disabled)
                        {
                            Passion bestPassion = Passion.None;
                            // Level already includes gene aptitudes. Passion is read from the learning rate it gives, so
                            // passions added by other mods count for what they actually do.
                            float levels = 0f, bestLearn = 0f;
                            foreach (SkillDef sd in wt.relevantSkills)
                            {
                                SkillRecord rec = p.skills.GetSkill(sd);
                                if (rec == null) continue;
                                levels += rec.Level;
                                float lf = 0.35f;
                                try { lf = rec.LearnRateFactor(true); } catch { }
                                if (lf > bestLearn) { bestLearn = lf; bestPassion = rec.passion; }
                            }
                            c.skill = levels / wt.relevantSkills.Count;
                            c.learnFactor = bestLearn;
                            c.passionBonus = Mathf.Clamp((bestLearn - 0.35f) / 1.15f * 6f, -3f, 8f);
                            c.passionLabel = PassionLabel(bestPassion);
                            c.expertise = ExpertiseBonus(expertise, wt, vseMult, out c.expertiseFrom);
                            c.health = health;
                            c.bonus = TraitGeneBonus(p, StatsFor(wt), out c.bonusFrom);
                            c.score = Mathf.Max(0f, c.skill + c.passionBonus) * health * c.bonus * c.expertise;
                        }
                        row.cells[t] = c;
                    }
                    rows.Add(row);
                }
            rows.SortBy(r => r.pawn.LabelShortCap);
            if (rows.Count == 0) return;
            HashSet<string> hot = Workload.Hot();
            try { RebuildRest(hot, tracker); }
            finally { if (sortByQuality) rows.SortByDescending(r => r.quality); }
        }

        static void RebuildRest(HashSet<string> hot, TimeUseTracker tracker)
        {

            for (int t = 0; t < types.Count; t++)
            {
                int tt = t;
                List<Cell> able = rows.Select(r => r.cells[tt]).Where(c => c.skilled && !c.disabled).OrderByDescending(c => c.score).ToList();
                for (int i = 0; i < able.Count; i++) { able[i].rank = i; able[i].capable = able.Count; }
            }

            // roles and skilled priorities
            foreach (PRow row in rows)
            {
                List<Cell> own = row.cells.Where(c => c.skilled && !c.disabled).OrderByDescending(c => c.score).ToList();
                for (int i = 0; i < own.Count; i++)
                {
                    Cell c = own[i];
                    c.ownRank = i;
                    c.role = c.score >= RoleFit && i < 3;
                    if (c.role && i == 0) c.sug = 1;                                   // their main role
                    else if (c.role) c.sug = 2;                                        // second and third roles
                    else if (c.rank == 0 && c.score >= GeneralistFit) c.sug = 2;       // the colony's best at it, even if it isn't their best
                    else if (c.score >= GeneralistFit || c.passionBonus >= 3f) c.sug = 3;
                    else if (c.skill < 3f) c.sug = 0;
                    else c.sug = 4;
                    if (c.kept) { c.sug = c.cur; c.role = c.role && c.cur >= 1 && c.cur <= 2; }   // kept cells count as they really are
                }
                float bestFit = own.Count > 0 ? own[0].score : 0f;
                bool anyUnskilled = row.cells.Any(c => !c.skilled && !c.fixedType && !c.disabled);
                row.support = bestFit < GeneralistFit && anyUnskilled;
                row.generalist = !row.support && bestFit < RoleFit;
            }

            // coverage: every skilled work type needs someone on it (two if it is falling behind)
            for (int t = 0; t < types.Count; t++)
            {
                List<Cell> able = rows.Select(r => r.cells[t]).Where(c => c.skilled && !c.disabled).OrderBy(c => c.rank).ToList();
                if (able.Count == 0) continue;
                int need = hot.Contains(types[t].defName) ? 2 : 1;
                int have = able.Count(c => c.sug == 1 || c.sug == 2);
                foreach (Cell c in able)
                {
                    if (have >= need) break;
                    if (c.sug == 1 || c.sug == 2 || c.kept) continue;
                    c.sug = 2;
                    have++;
                }
            }

            // Workload cap: only as many colonists get priority 1 on a skilled job as the last day's work justifies
            // (one per 8 colonist-hours actually spent on it, plus one if it is falling behind). The strongest keep
            // priority 1; the rest drop to 2 as backups and their next role, if it has room, moves up to 1.
            dayHours.Clear();
            slots.Clear();
            bool haveDay = tracker != null && tracker.backlog.Count >= 20;
            if (haveDay)
            {
                for (int t = 0; t < types.Count; t++)
                {
                    if (NoSkill(types[t]) || FixedTypes.Contains(types[t].defName)) continue;
                    float h = (float)tracker.ColonyTicks("wt:" + types[t].defName, 24) / GenDate.TicksPerHour;
                    dayHours[types[t]] = h;
                    slots[types[t]] = Mathf.Max(1, Mathf.CeilToInt(h / HoursPerWorker)) + (Workload.Level(types[t].defName) >= 3 ? 1 : 0);
                }
                for (int pass = 0; pass < 3; pass++)
                {
                    bool changed = false;
                    for (int t = 0; t < types.Count; t++)
                    {
                        if (!slots.TryGetValue(types[t], out int cap)) continue;
                        List<PRow> ones = rows.Where(r => r.cells[t].skilled && !r.cells[t].disabled && r.cells[t].sug == 1)
                            .OrderByDescending(r => r.cells[t].kept ? 1 : 0).ThenByDescending(r => r.cells[t].score).ToList();   // kept and title holders fill slots first; they are never moved
                        for (int i = cap; i < ones.Count; i++)
                        {
                            PRow row = ones[i];
                            if (row.cells[t].kept) continue;
                            row.cells[t].sug = 2;
                            row.cells[t].backup = true;
                            changed = true;
                            // move their next role up, if that job has room
                            foreach (Cell next in row.cells.Where(c => c.role && !c.backup && !c.kept && c.sug == 2).OrderBy(c => c.ownRank))
                            {
                                int nt = types.IndexOf(next.wt);
                                if (!slots.TryGetValue(next.wt, out int ncap) || rows.Count(r => r.cells[nt].sug == 1 && r.cells[nt].skilled && !r.cells[nt].disabled) >= ncap) continue;
                                next.sug = 1;
                                break;
                            }
                        }
                    }
                    if (!changed) break;
                }
            }

            // Skilled work that is behind, two different cures:
            //  - Behind despite steady work on it: it needs more workers. Colonists with a passion, an expertise or
            //    a decent level are added, best fit first: to 1 if it is at least as good as anything else they do
            //    at 1, otherwise to 2 so their stronger work still comes first.
            //  - Waiting a day with nobody working on it: the people assigned never get to it, so adding more at a low
            //    priority won't help. One capable colonist with time to spare gets it as a priority-1 job instead.
            for (int t = 0; t < types.Count; t++)
            {
                int lvl = Workload.Level(types[t].defName);
                if (lvl < 3 || NoSkill(types[t]) || FixedTypes.Contains(types[t].defName)) continue;
                if (Workload.Unattended(types[t].defName))
                {
                    float Idle(PRow r) => Mathf.Max(0f, tracker?.Share(r.pawn, "idle", 24) ?? 0f);
                    PRow pick = rows.Where(r =>
                        {
                            Cell c = r.cells[t];
                            return c.skilled && !c.disabled && !c.kept && (c.score >= GeneralistFit || c.passionBonus > 0.5f)
                                && !r.cells.Any(o => o != c && o.skilled && !o.disabled && o.sug == 1 && hot.Contains(o.wt.defName) && !Workload.Unattended(o.wt.defName));
                        })
                        .OrderByDescending(r => r.cells[t].sug == 1 ? -1f : Idle(r) * 20f + r.cells[t].score).FirstOrDefault();
                    if (pick != null && pick.cells[t].sug != 1)
                    {
                        // their other priority-1 skilled work steps down to 2, so this really is their first job
                        foreach (Cell o in pick.cells) if (o.skilled && !o.disabled && !o.kept && o.sug == 1) { o.sug = 2; o.backup = true; }
                        pick.cells[t].sug = 1;
                        pick.cells[t].reassigned = true;
                    }
                    continue;
                }
                int toAdd = lvl >= 4 ? 2 : 1;
                foreach (PRow row in rows.OrderByDescending(r => r.cells[t].score))
                {
                    if (toAdd <= 0) break;
                    Cell c = row.cells[t];
                    if (!c.skilled || c.disabled || c.kept || c.sug == 1 || c.score < GeneralistFit) continue;
                    if (!(c.passionBonus > 0.5f || c.expertiseFrom != null || c.skill >= 8f)) continue;
                    bool hasBetter = row.cells.Any(o => o != c && o.skilled && !o.disabled && o.sug == 1 && o.score > c.score);
                    int target = hasBetter ? 2 : 1;
                    if (c.sug != 0 && c.sug <= target) continue;
                    c.sug = target;
                    c.promoted = true;
                    toAdd--;
                }
            }

            // Reserved benches: when a strong colonist runs a workbench job at priority 1, weaker colonists are kept
            // off it so they don't occupy the bench or use up materials. Others stay on only if they are also at
            // priority 1, or have a passion or an expertise for it. Not applied while that work is falling behind.
            var benchTypes = new HashSet<WorkTypeDef>(Workload.EnsureBillTypes().Values);
            for (int t = 0; t < types.Count; t++)
            {
                if (!benchTypes.Contains(types[t]) || hot.Contains(types[t].defName)) continue;
                List<PRow> leads = rows.Where(r => r.cells[t].skilled && !r.cells[t].disabled && r.cells[t].sug == 1 && r.cells[t].score >= RoleFit).ToList();
                if (leads.Count == 0) continue;
                string names = string.Join(", ", leads.Take(3).Select(r => r.pawn.LabelShortCap)) + (leads.Count > 3 ? " and others" : "");
                foreach (PRow row in rows)
                {
                    Cell c = row.cells[t];
                    if (!c.skilled || c.disabled || c.sug == 1 || c.sug == 0) continue;
                    if (c.passionBonus > 0.5f || c.expertiseFrom != null || c.backup || c.kept) continue;   // backups are good enough for priority 1, there just isn't the work
                    c.sug = 0;
                    c.role = false;
                    c.reservedFor = names;
                }
            }

            // Worker quality: how valuable a colonist's skilled work is to this colony right now. Each of their three
            // best skilled fits counts (full, half, quarter), weighted by how much the colony needs that work:
            // more if it is falling behind, more if few colonists can hold that role. Unskilled work never counts.
            var needOf = new Dictionary<WorkTypeDef, float>();
            for (int t = 0; t < types.Count; t++)
            {
                int holders = rows.Count(r => r.cells[t].role);
                int lvl = Workload.Level(types[t].defName);
                needOf[types[t]] = 1f + (lvl >= 4 ? 1f : lvl == 3 ? 0.75f : lvl == 2 ? 0.4f : 0f) + (holders == 0 ? 0.5f : holders == 1 ? 0.25f : 0f);
            }
            float[] weights = { 1f, 0.5f, 0.25f };
            foreach (PRow row in rows)
            {
                var top = row.cells.Where(c => c.skilled && !c.disabled && c.score > 0f).OrderByDescending(c => c.score * needOf[c.wt]).Take(3).ToList();
                row.quality = 0f;
                var parts = new List<string>();
                for (int i = 0; i < top.Count; i++)
                {
                    float pts = top[i].score * needOf[top[i].wt] * weights[i];
                    row.quality += pts;
                    parts.Add(RoleName(top[i].wt) + ": fit " + top[i].score.ToString("0.0") + " x need " + needOf[top[i].wt].ToString("0.##") + " x " + weights[i].ToString("0.##") + " = " + pts.ToString("0.0"));
                }
                row.qualityTip = parts.Count == 0 ? "No skilled work they can do." : string.Join("\n", parts);
            }
            float bestQuality = Mathf.Max(1f, rows.Max(r => r.quality));
            List<PRow> byQuality = rows.OrderBy(r => r.quality).ToList();
            var tier = new Dictionary<PRow, int>();      // 0 = lowest third, 2 = highest third
            for (int i = 0; i < byQuality.Count; i++) tier[byQuality[i]] = Mathf.Min(2, i * 3 / byQuality.Count);
            foreach (PRow row in rows)
            {
                row.qualityTip = "Worker quality " + (row.quality / bestQuality * 100f).ToString("0") + " of 100 (the colony's most valuable skilled worker = 100).\n\n" + row.qualityTip
                    + "\n\nNeed is higher for work that is falling behind and for roles few colonists can hold. Unskilled work goes to the lowest scores first.";
                row.quality = row.quality / bestQuality * 100f;
            }

            // Striping: every unskilled job that isn't getting done (Slow or worse on the Workload page) gets its own
            // person at priority 1, a different person for each job, so hauling, cleaning and the rest are each
            // somebody's first job instead of everybody's last. Colonists with no skilled work at priority 1 or 2 are
            // used first, then the lower two thirds by worker quality (never someone whose own priority-1 work is
            // falling behind). Who covers which job moves along by one each day.
            var rotation = new Dictionary<PRow, WorkTypeDef>();
            List<int> neglected = Enumerable.Range(0, types.Count)
                .Where(t => !FixedTypes.Contains(types[t].defName) && NoSkill(types[t])
                            && Workload.Level(types[t].defName) >= 2 && (tracker == null || !tracker.IsResting(types[t].defName)))
                .OrderByDescending(t => Workload.Level(types[t].defName)).ToList();
            if (neglected.Count > 0)
            {
                int day = Find.TickManager.TicksGame / GenDate.TicksPerDay;
                bool Free(PRow r) => !r.cells.Any(c => c.skilled && !c.disabled && (c.sug == 1 || c.sug == 2));
                bool BusyOnHot(PRow r) => r.cells.Any(c => c.skilled && !c.disabled && c.sug == 1 && hot.Contains(c.wt.defName));
                List<PRow> pool = byQuality.Where(r => r.titleName == null && (r.support || tier[r] <= 1) && !BusyOnHot(r))
                    .OrderBy(r => Free(r) ? 0 : 1).ThenBy(r => r.quality).ToList();
                if (pool.Count > 0)
                {
                    // one dedicated person per neglected job
                    for (int k = 0; k < neglected.Count; k++)
                        for (int j = 0; j < pool.Count; j++)
                        {
                            PRow r = pool[(k + day + j) % pool.Count];
                            Cell c = r.cells[neglected[k]];
                            if (rotation.ContainsKey(r) || c.disabled || c.kept) continue;
                            rotation[r] = types[neglected[k]];
                            break;
                        }
                    // anyone left with no skilled work at all joins in, spread across the jobs
                    int n = 0;
                    foreach (PRow r in pool.Where(r => Free(r) && (r.support || tier[r] == 0) && !rotation.ContainsKey(r)))
                    {
                        for (int k = 0; k < neglected.Count; k++)
                        {
                            int t = neglected[(n + day + k) % neglected.Count];
                            if (r.cells[t].disabled || r.cells[t].kept) continue;
                            rotation[r] = types[t];
                            break;
                        }
                        n++;
                    }
                }
            }

            // care work stays off colonists with a medical role, as long as somebody else is free to do it
            var careCovered = new HashSet<WorkTypeDef>();
            foreach (PRow row in rows)
            {
                if (row.cells.Any(c => c.role && MedicalRoleTypes.Contains(c.wt.defName))) continue;
                foreach (Cell c in row.cells)
                    if (CareTypes.Contains(c.wt.defName) && !c.disabled && !(c.kept && c.cur == 0)) careCovered.Add(c.wt);
            }

            foreach (PRow row in rows)
            {
                float idle = tracker != null ? tracker.Share(row.pawn, "idle", 24) : -1f;
                rotation.TryGetValue(row, out WorkTypeDef rotType);
                bool medic = row.cells.Any(c => c.role && MedicalRoleTypes.Contains(c.wt.defName));
                JobTitle jobTitle = tracker?.TitleFor(row.pawn);
                // unskilled work goes to the lowest worker-quality scores first
                int unskilled = row.support || tier[row] == 0 ? 2 : tier[row] == 1 ? 3 : idle >= 0.2f ? 3 : 4;
                foreach (Cell c in row.cells)
                {
                    if (c.disabled) { c.sug = 0; continue; }
                    if (c.fixedType) c.sug = c.cur;
                    else if (!c.skilled)
                    {
                        c.rotation = rotType == c.wt;
                        c.sug = c.rotation ? 1 : unskilled;
                        if (medic && !c.rotation && CareTypes.Contains(c.wt.defName) && careCovered.Contains(c.wt)) c.sug = 0;     // doctors treat; others feed and carry
                    }
                    if (c.kept) c.sug = c.cur;
                    if (tracker != null && tracker.IsResting(c.wt.defName)) { c.resting = true; c.sug = c.cur; }
                }
                row.changes = row.cells.Count(c => !c.disabled && c.sug != c.cur);

                List<Cell> roles = row.cells.Where(c => c.role).OrderBy(c => c.ownRank).ToList();
                Cell best = row.cells.Where(c => c.skilled && !c.disabled).OrderByDescending(c => c.score).FirstOrDefault();
                float move = row.pawn.GetStatValue(StatDefOf.MoveSpeed) / 4.6f;
                string idleText = idle >= 0f ? " Idle " + idle.ToStringPercent("0") + " of the last day." : "";
                if (roles.Count > 0)
                {
                    row.role = string.Join(", ", roles.Select(c => RoleName(c.wt)));
                    row.roleTip = string.Join("\n", roles.Select((c, i) => (i == 0 ? "Main role: " : "Also: ") + RoleName(c.wt) + " (fit " + c.score.ToString("0.0") + ", #" + (c.rank + 1) + " of " + c.capable + " in the colony)"))
                        + "\n\nSuggestion: main role at priority 1, other roles at 2, unskilled work at " + unskilled + " (set by their worker quality score)." + idleText;
                }
                else if (row.generalist)
                {
                    row.role = "Generalist";
                    row.roleTip = "Decent at some skilled work but not strong enough to hold a role"
                        + (best != null ? " (best fit: " + RoleName(best.wt) + " " + best.score.ToString("0.0") + ")" : "")
                        + ".\n\nSuggestion: skilled work they are decent at at priority 3, unskilled work at " + unskilled + " (set by their worker quality score)." + idleText;
                }
                else if (row.support)
                {
                    row.role = "Support";
                    row.roleTip = "Best used for unskilled work (hauling, cleaning and similar): no skilled work fits them"
                        + (best != null ? " (best fit: " + RoleName(best.wt) + " " + best.score.ToString("0.0") + ")" : "")
                        + ". Walks at " + move.ToStringPercent("0") + " of normal speed." + idleText
                        + "\n\nSuggestion: unskilled work at priority 2.";
                }
                else { row.role = "-"; row.roleTip = "Cannot do unskilled work and has no strong skilled fit."; }
                if (jobTitle != null)
                {
                    row.roleTip = "Job title: " + jobTitle.name + ". The advisor and the automatic features don't change this colonist.\n\nWhat the advisor would have called them: " + row.role + "\n" + row.roleTip;
                    row.role = "\"" + jobTitle.name + "\"";
                }
            }
        }

        // Colonists who could take on more of a work type: able, not already on it at priority 1-2, best fit first.
        public static List<Pawn> Candidates(WorkTypeDef wt, int count)
        {
            int now = Find.TickManager.TicksGame / 250;
            if (stamp != now) { Rebuild(); stamp = now; }
            int t = types.IndexOf(wt);
            if (t < 0) return new List<Pawn>();
            TimeUseTracker tracker = TimeUseTracker.Instance;
            float Idle(PRow r) => Mathf.Max(0f, tracker?.Share(r.pawn, "idle", 24) ?? 0f);
            var open = rows.Where(r => !r.cells[t].disabled && !r.cells[t].kept && (r.cells[t].cur == 0 || r.cells[t].cur >= 3));
            bool skilled = rows.Count > 0 && rows[0].cells[t].skilled;
            if (skilled)   // skilled work: only colonists who are at least decent at it, best fit first
                return open.Where(r => r.cells[t].score >= GeneralistFit).OrderByDescending(r => r.cells[t].score).Take(count).Select(r => r.pawn).ToList();
            // unskilled work: support workers first, then generalists, then role-holders only if they are standing around
            float cutoff = rows.Select(r => r.quality).OrderBy(q => q).ElementAt(Mathf.Min(rows.Count - 1, rows.Count * 2 / 3));
            return open.Where(r => r.support || r.quality < cutoff || Idle(r) >= 0.2f)   // never the top third unless they are idle
                .OrderBy(r => r.quality - 30f * Idle(r))
                .Take(count).Select(r => r.pawn).ToList();
        }

        // ---- read access for the Job titles page ----
        static void Ensure()
        {
            int now = Find.TickManager.TicksGame / 250;
            if (stamp != now) { Rebuild(); stamp = now; }
        }
        public static void Invalidate() => stamp = -1;
        public static List<WorkTypeDef> Types { get { Ensure(); return types; } }
        public static bool IsFixed(WorkTypeDef wt) => FixedTypes.Contains(wt.defName);
        public static bool IsSkilled(WorkTypeDef wt) => !IsFixed(wt) && !NoSkill(wt);
        public static List<Pawn> Pawns { get { Ensure(); return rows.Select(r => r.pawn).ToList(); } }
        static PRow RowOf(Pawn p) { Ensure(); for (int i = 0; i < rows.Count; i++) if (rows[i].pawn == p) return rows[i]; return null; }
        public static float Score(Pawn p, WorkTypeDef wt)
        {
            PRow r = RowOf(p); int t = types.IndexOf(wt);
            return r == null || t < 0 || !r.cells[t].skilled || r.cells[t].disabled ? 0f : r.cells[t].score;
        }
        public static bool CanDo(Pawn p, WorkTypeDef wt) { PRow r = RowOf(p); int t = types.IndexOf(wt); return r != null && t >= 0 && !r.cells[t].disabled; }
        public static float Quality(Pawn p) => RowOf(p)?.quality ?? 0f;
        public static string RoleText(Pawn p) => RowOf(p)?.role ?? "-";

        // The advisor's suggested raises for one work type: who, from what, to what.
        public static List<(Pawn pawn, int cur, int sug)> Suggested(WorkTypeDef wt, int count)
        {
            int now = Find.TickManager.TicksGame / 250;
            if (stamp != now) { Rebuild(); stamp = now; }
            int t = types.IndexOf(wt);
            if (t < 0) return new List<(Pawn, int, int)>();
            return rows.Where(r => !r.cells[t].disabled && r.cells[t].sug > 0 && (r.cells[t].cur == 0 || r.cells[t].sug < r.cells[t].cur))
                .OrderBy(r => r.cells[t].sug).ThenByDescending(r => r.cells[t].skilled ? r.cells[t].score : -r.quality)
                .Take(count).Select(r => (r.pawn, r.cells[t].cur, r.cells[t].sug)).ToList();
        }

        public static void ApplyCells(WorkTypeDef wt, List<Pawn> pawns)
        {
            int t = types.IndexOf(wt);
            if (t < 0) return;
            undo = new Dictionary<Pawn, Dictionary<WorkTypeDef, int>>();
            if (!Find.PlaySettings.useWorkPriorities) Find.PlaySettings.useWorkPriorities = true;
            foreach (PRow row in rows)
            {
                Cell c = row.cells[t];
                if (!pawns.Contains(row.pawn) || c.disabled || c.sug == c.cur) continue;
                undo[row.pawn] = new Dictionary<WorkTypeDef, int> { { wt, c.cur } };
                row.pawn.workSettings.SetPriorityQuiet(wt, c.sug);
            }
            stamp = -1;
        }

        public static int WorkersOn(WorkTypeDef wt)
        {
            int now = Find.TickManager.TicksGame / 250;
            if (stamp != now) { Rebuild(); stamp = now; }
            int t = types.IndexOf(wt);
            return t < 0 ? 0 : rows.Count(r => !r.cells[t].disabled && r.cells[t].cur >= 1 && r.cells[t].cur <= 2);
        }

        public static void Raise(WorkTypeDef wt, List<Pawn> pawns, int priority)
        {
            undo = new Dictionary<Pawn, Dictionary<WorkTypeDef, int>>();
            if (!Find.PlaySettings.useWorkPriorities) Find.PlaySettings.useWorkPriorities = true;
            foreach (Pawn p in pawns)
            {
                if (p?.workSettings == null || p.WorkTypeIsDisabled(wt)) continue;
                undo[p] = new Dictionary<WorkTypeDef, int> { { wt, p.workSettings.GetPriority(wt) } };
                p.workSettings.SetPriorityQuiet(wt, priority);
            }
            stamp = -1;
        }

        public static bool CanUndo => undo != null;
        public static void UndoLast() => Undo();

        static void Apply(IEnumerable<PRow> which)
        {
            undo = new Dictionary<Pawn, Dictionary<WorkTypeDef, int>>();
            if (!Find.PlaySettings.useWorkPriorities) Find.PlaySettings.useWorkPriorities = true;
            foreach (PRow row in which)
            {
                var before = new Dictionary<WorkTypeDef, int>();
                foreach (Cell c in row.cells)
                {
                    if (c.disabled || c.sug == c.cur) continue;
                    before[c.wt] = c.cur;
                    row.pawn.workSettings.SetPriorityQuiet(c.wt, c.sug);
                }
                if (before.Count > 0) undo[row.pawn] = before;
            }
            stamp = -1;
        }

        static void Undo()
        {
            if (undo == null) return;
            foreach (var kv in undo)
            {
                if (kv.Key == null || kv.Key.Dead || kv.Key.workSettings == null) continue;
                foreach (var w in kv.Value)
                    if (!kv.Key.WorkTypeIsDisabled(w.Key)) kv.Key.workSettings.SetPriorityQuiet(w.Key, w.Value);
            }
            undo = null;
            stamp = -1;
        }

        static string CellTip(PRow row, Cell c)
        {
            string name = row.pawn.LabelShortCap + " - " + c.wt.labelShort.CapitalizeFirst();
            if (c.disabled) return name + "\nCannot do this work.";
            string s = name + "\nCurrent priority: " + (c.cur == 0 ? "off" : c.cur.ToString()) + "\nSuggested: " + (c.sug == 0 ? "off" : c.sug.ToString());
            if (c.title != null) return s + "\n\nThis colonist holds the job title \"" + c.title + "\", so the advisor and the automatic features leave all their priorities alone. Change them on the Job titles page.";
            if (c.kept) return s + "\n\nKept as you set it. Click to let the advisor suggest again.";
            if (c.resting) return s + "\n\nThis work type is resting: nothing to do for 3 days, so it was switched off. It comes back by itself when work appears.";
            if (c.fixedType) s += "\n\nThe advisor leaves this work type alone.";
            else if (c.skilled)
            {
                s += "\n\nFit for " + RoleName(c.wt) + ": " + c.score.ToString("0.0") + (c.role ? "  (holds this role)" : "")
                   + "\n  Skill " + c.skill.ToString("0.#") + " (including gene aptitudes)"
                   + "\n  Passion " + (c.passionBonus >= 0 ? "+" : "") + c.passionBonus.ToString("0.#") + " (" + c.passionLabel + ", learns at x" + c.learnFactor.ToString("0.##") + ")"
                   + "\n  Health and work speed x" + c.health.ToString("0.##")
                   + "\n  Traits and genes x" + c.bonus.ToString("0.##") + (c.bonusFrom != null ? " (" + c.bonusFrom + ")" : "")
                   + (c.expertiseFrom != null ? "\n  Expertise x" + c.expertise.ToString("0.##") + " (" + c.expertiseFrom + ")" : "")
                   + "\nRanked #" + (c.rank + 1) + " of " + c.capable + " in the colony, #" + (c.ownRank + 1) + " among their own";
                List<StatDef> st = StatsFor(c.wt);
                if (st.Count > 0) s += "\nStats checked: " + string.Join(", ", st.Select(x => x.label));
                if (Workload.Hot().Contains(c.wt.defName)) s += "\nThis work is falling behind (see Workload), so a second colonist is suggested for it.";
                if (slots.TryGetValue(c.wt, out int cap)) s += "\nLast day: " + dayHours[c.wt].ToString("0.#") + " colonist-hours of this work, enough for " + cap + " at priority 1.";
                if (c.reassigned) s += "\nReassigned: this work has waited a day with nobody working on it, so the people who had it never got to it. This colonist can do it and has the time, so it becomes their first job and their other priority-1 work steps down to 2.";
                if (c.promoted) s += "\nAdded: this work is behind even though people are working on it, so it needs more workers. This colonist has the skill, passion or expertise for it. It stays below any stronger work they have.";
                if (c.backup) s += "\nBackup at priority 2: good enough for priority 1, but it was moved down, either because there isn't enough of this work for another full-time worker or because this colonist was given neglected work as their first job.";
                if (c.reservedFor != null) s += "\nLeft off: " + c.reservedFor + " run" + (c.reservedFor.Contains(",") || c.reservedFor.Contains(" and ") ? "" : "s") + " this at priority 1, and this colonist has no passion or expertise for it, so the bench is kept free for the better worker.";
                else if (c.sug == 0) s += "\nToo unskilled to be worth it.";
            }
            else if (c.rotation) s += "\n\nPriority 1 for today: this unskilled work isn't getting done, so this colonist is its dedicated person. Each neglected job gets a different colonist, and who covers which one moves along by one each day.";
            else s += "\n\nUnskilled work. " + (row.support ? "This colonist is a good support worker." : row.generalist ? "This colonist is a generalist." : "This colonist is more useful on skilled work.");
            return s + "\n\nClick to keep the current priority for this cell.";
        }

        public static void Draw(Rect inRect)
        {
            int now = Find.TickManager.TicksGame / 250;
            if (stamp != now) { Rebuild(); stamp = now; }

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            int totalChanges = rows.Sum(r => r.changes);
            Rect allRect = new Rect(inRect.x, inRect.y, 210f, 28f);
            if (Widgets.ButtonText(allRect, "Apply all (" + totalChanges + " change" + (totalChanges == 1 ? "" : "s") + ")", true, true, totalChanges > 0))
                Apply(rows.ToList());
            Rect undoRect = new Rect(allRect.xMax + 6f, inRect.y, 150f, 28f);
            if (Widgets.ButtonText(undoRect, "Undo last apply", true, true, undo != null)) Undo();
            TimeUseTracker trk = TimeUseTracker.Instance;
            if (trk != null)
            {
                Rect autoRect = new Rect(undoRect.xMax + 14f, inRect.y, 150f, 28f);
                bool was = trk.autoAssign;
                Widgets.CheckboxLabeled(autoRect, "Daily review", ref trk.autoAssign);
                if (trk.autoAssign && !was) trk.lastReviewDay = Find.TickManager.TicksGame / GenDate.TicksPerDay;
                TooltipHandler.TipRegion(autoRect, "Once a day the advisor recalculates from the latest skills, time use and workload (including work that fell behind) and applies its suggestions by itself. You get a letter listing what changed, and Undo still works. Cells you marked to keep are never touched.");
                Rect restRect = new Rect(autoRect.xMax + 10f, inRect.y, 170f, 28f);
                bool wasRest = trk.restUnused;
                Widgets.CheckboxLabeled(restRect, "Rest unused work", ref trk.restUnused);
                if (wasRest && !trk.restUnused) { HrAutomation.WakeAll(trk); stamp = -1; }
                TooltipHandler.TipRegion(restRect, "When a kind of work has had nothing waiting and nobody working on it for 3 days, switch it off for everyone so idle colonists stop checking for it. It is switched back on, with everyone's old priority, within an hour of work appearing. Medical, firefighting, rescue, childcare and warden work are never rested.");
                undoRect = restRect;
            }
            GUI.color = Color.gray;
            string note = Find.PlaySettings.useWorkPriorities
                ? "\"3>1\" = change 3 to 1. Click a cell to keep it."
                : "Manual priorities are off in the Work tab. Applying will turn them on.";
            Widgets.Label(new Rect(undoRect.xMax + 14f, inRect.y, inRect.xMax - undoRect.xMax - 14f, 28f), note);
            GUI.color = Color.white;

            float y = inRect.y + 34f;
            if (rows.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 60f), "No colonists on a map.");
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            const float nameW = 140f, qualW = 54f, roleW = 150f + qualW, applyW = 64f, rowH = 26f, headH = 24f;
            float cw = Mathf.Max(28f, (inRect.width - 16f - nameW - roleW - applyW) / Mathf.Max(1, types.Count));
            float tableW = nameW + roleW + cw * types.Count + applyW;
            Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
            Rect view = new Rect(0f, 0f, Mathf.Max(tableW, outRect.width - 16f), headH + rows.Count * rowH);
            if (HrSelection.TakeScroll(1))
            {
                int at = rows.FindIndex(r => r.pawn != null && r.pawn.thingIDNumber == HrSelection.PawnId);
                if (at >= 0) scroll.y = Mathf.Clamp(at * rowH - (outRect.height - headH) / 2f + rowH, 0f, Mathf.Max(0f, view.height - outRect.height));
            }
            Widgets.BeginScrollView(outRect, ref scroll, view);

            Text.WordWrap = false;
            for (int i = 0; i < rows.Count; i++)
            {
                float ry = headH + i * rowH;
                if (ry + rowH < scroll.y + headH || ry > scroll.y + outRect.height) continue;
                PRow row = rows[i];
                if (i % 2 == 1) Widgets.DrawLightHighlight(new Rect(0f, ry, view.width, rowH));
                if (row.pawn.thingIDNumber == HrSelection.PawnId) Widgets.DrawHighlightSelected(new Rect(0f, ry, view.width, rowH));

                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleLeft;
                Rect nameRect = new Rect(applyW + 4f, ry, nameW - 4f, rowH);
                Widgets.Label(nameRect, row.pawn.LabelShortCap);
                Widgets.DrawHighlightIfMouseover(nameRect);
                if (Widgets.ButtonInvisible(nameRect)) CameraJumper.TryJumpAndSelect(row.pawn);

                Text.Font = GameFont.Small;
                Rect qualRect = new Rect(applyW + nameW, ry, qualW - 6f, rowH);
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(qualRect, row.quality.ToString("0"));
                Text.Anchor = TextAnchor.MiddleLeft;
                TooltipHandler.TipRegion(qualRect, row.qualityTip);
                Text.Font = GameFont.Tiny;
                Rect roleRect = new Rect(applyW + nameW + qualW, ry, roleW - qualW, rowH);
                if (row.titleName != null && !row.reviewDone)
                {
                    row.reviewDone = true;
                    try { row.review = JobTitles.ReviewFor(TimeUseTracker.Instance, row.pawn, out row.reviewConcern); } catch { }
                }
                GUI.color = row.reviewConcern ? new Color(0.95f, 0.60f, 0.25f) : row.titleName != null ? new Color(0.75f, 0.9f, 0.75f) : row.support ? new Color(0.95f, 0.85f, 0.45f) : row.generalist ? new Color(0.8f, 0.8f, 0.8f) : new Color(0.6f, 0.8f, 1f);
                Widgets.Label(roleRect, row.reviewConcern ? row.role + " (!)" : row.role);
                GUI.color = Color.white;
                TooltipHandler.TipRegion(roleRect, row.review != null ? (row.reviewConcern ? "HR recommends looking at this title assignment:\n" : "Title review: ") + row.review + "\n\n" + row.roleTip : row.roleTip);

                Text.Anchor = TextAnchor.MiddleCenter;
                for (int t = 0; t < types.Count; t++)
                {
                    Cell c = row.cells[t];
                    Rect cell = new Rect(applyW + nameW + roleW + t * cw + 1f, ry + 2f, cw - 2f, rowH - 4f);
                    if (c.disabled)
                    {
                        Widgets.DrawBoxSolid(cell, new Color(0f, 0f, 0f, 0.35f));
                    }
                    else
                    {
                        bool differs = c.sug != c.cur;
                        if (differs)
                        {
                            bool raise = c.sug > 0 && (c.cur == 0 || c.sug < c.cur);
                            Widgets.DrawBoxSolid(cell, raise ? new Color(0.20f, 0.50f, 0.22f, 0.75f) : new Color(0.55f, 0.22f, 0.20f, 0.75f));
                            Widgets.Label(cell, c.cur + ">" + c.sug);
                        }
                        else
                        {
                            GUI.color = c.cur == 0 ? new Color(1f, 1f, 1f, 0.25f) : new Color(1f, 1f, 1f, 0.7f);
                            Widgets.Label(cell, c.cur == 0 ? "-" : c.cur.ToString());
                            GUI.color = Color.white;
                        }
                        if (c.kept) Widgets.DrawBox(cell);
                        if (!c.fixedType && Widgets.ButtonInvisible(cell))
                        {
                            string k = Key(row.pawn, c.wt);
                            if (!kept.Remove(k)) kept.Add(k);
                            stamp = -1;
                        }
                    }
                    if (Mouse.IsOver(cell))
                    {
                        Widgets.DrawHighlight(cell);
                        TooltipHandler.TipRegion(cell, CellTip(row, c));
                    }
                }

                Text.Font = GameFont.Tiny;
                Rect applyRect = new Rect(2f, ry + 2f, applyW - 6f, rowH - 4f);
                if (row.changes > 0 && Widgets.ButtonText(applyRect, "Apply " + row.changes)) Apply(new[] { row });
            }
            // header (drawn last so it stays on top while scrolling)
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Text.WordWrap = false;
            Rect qh = new Rect(applyW + nameW, scroll.y, qualW, headH);
            Widgets.DrawBoxSolid(new Rect(0f, scroll.y, applyW + nameW + roleW, headH), new Color(0.13f, 0.14f, 0.16f));
            if (sortByQuality) Widgets.DrawHighlightSelected(qh);
            Widgets.Label(qh, "Quality");
            Widgets.Label(new Rect(applyW + nameW + qualW, scroll.y, roleW - qualW, headH), "Role");
            TooltipHandler.TipRegion(qh, "Worker quality: how valuable this colonist's skilled work is to the colony right now (0-100). Click to sort by it.");
            if (Widgets.ButtonInvisible(qh)) { sortByQuality = !sortByQuality; stamp = -1; }
            for (int t = 0; t < types.Count; t++)
            {
                Rect h = new Rect(applyW + nameW + roleW + t * cw, scroll.y, cw, headH);
                Widgets.DrawBoxSolid(h, new Color(0.13f, 0.14f, 0.16f));
                string lab = types[t].labelShort.CapitalizeFirst();
                int max = Mathf.Max(2, Mathf.FloorToInt(cw / 6.5f));
                Widgets.Label(h, lab.Length > max ? lab.Substring(0, max) : lab);
                TooltipHandler.TipRegion(h, lab + (slots.TryGetValue(types[t], out int hcap) ? "\nLast day: " + dayHours[types[t]].ToString("0.#") + " colonist-hours, enough for " + hcap + " at priority 1." : "") + (FixedTypes.Contains(types[t].defName) ? "\nLeft alone by the advisor." : BenchNoSkill().Contains(types[t].defName) ? "\nNone of this work's recipes use or train a skill, so it is treated as unskilled." : CareTypes.Contains(types[t].defName) ? "\nCare work (feeding, carrying and visiting patients). It doesn't train Medicine, so it is treated as unskilled and kept off colonists with a medical role." : types[t].relevantSkills.NullOrEmpty() ? "\nUnskilled work." : "\nSkills: " + string.Join(", ", types[t].relevantSkills.Select(s => s.label))));
            }

            Text.WordWrap = true;
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
