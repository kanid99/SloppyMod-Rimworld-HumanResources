using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace SloppyHR
{
    public static class HR
    {
        public const string Tag = "[SloppyMods HR] ";
        public const string HarmonyId = "sloppymod.humanresources";

        public static string Reason(Exception e)
        {
            while (e?.InnerException != null) e = e.InnerException;
            return e == null ? "unknown error" : e.GetType().Name + ": " + e.Message;
        }
    }

    public class HumanResourcesMod : Mod
    {
        public static HumanResourcesMod Instance;
        public static HrSettings Hr;

        public HumanResourcesMod(ModContentPack content) : base(content)
        {
            Instance = this;
            try { Hr = GetSettings<HrSettings>(); } catch (Exception e) { Log.Warning(HR.Tag + "settings could not be loaded: " + e.Message); }
            if (Hr == null) Hr = new HrSettings();
            PrototypeMigration.ImportSettings(this);
            PrototypeMigration.PatchSaveLoading();
        }

        public override string SettingsCategory() => "SloppyMods - Human Resources";

        public override void DoSettingsWindowContents(Rect inRect) => HrSettingsUI.Draw(inRect);
    }

    // This mod was prototyped inside a private compatibility mod, under the namespace "Harmony3Compat".
    // These two steps carry that data over: the job title library and workload exclusions from its settings file,
    // and the HR data inside saved games.
    public static class PrototypeMigration
    {
        const string OldNamespace = "Harmony3Compat.";

        public static void ImportSettings(HumanResourcesMod mod)
        {
            try
            {
                HrSettings hr = HumanResourcesMod.Hr;
                if (hr.importedPrototype) return;
                hr.importedPrototype = true;
                string old = Path.Combine(GenFilePaths.ConfigFolderPath, "Mod_Harmony3Compat_Harmony3CompatMod.xml");
                if (File.Exists(old))
                {
                    XElement s = XDocument.Load(old).Root?.Element("ModSettings");
                    if (s != null)
                    {
                        foreach (XElement li in s.Element("excludedBenches")?.Elements("li") ?? Enumerable.Empty<XElement>()) hr.excludedBenches.Add(li.Value);
                        foreach (XElement li in s.Element("excludedRecipes")?.Elements("li") ?? Enumerable.Empty<XElement>()) hr.excludedRecipes.Add(li.Value);
                        int titles = 0;
                        foreach (XElement li in s.Element("titleLibrary")?.Elements("li") ?? Enumerable.Empty<XElement>())
                        {
                            string name = li.Element("name")?.Value ?? "Title";
                            if (hr.titleLibrary.Any(x => x.name == name)) continue;
                            JobTitle jt = new JobTitle { id = 0, name = name };
                            foreach (XElement p in li.Element("prio")?.Elements("li") ?? Enumerable.Empty<XElement>())
                            {
                                string[] parts = p.Value.Split(';');
                                if (parts.Length == 2 && int.TryParse(parts[1], out int v)) jt.prio[parts[0]] = v;
                            }
                            hr.titleLibrary.Add(jt);
                            titles++;
                        }
                        Log.Message(HR.Tag + "Imported the prototype's settings: " + titles + " library titles, " + hr.excludedBenches.Count + " excluded workbenches, " + hr.excludedRecipes.Count + " excluded bills.");
                    }
                }
                mod.WriteSettings();
            }
            catch (Exception e) { Log.Warning(HR.Tag + "could not import the prototype's settings: " + HR.Reason(e)); }
        }

        // Saved games made with the prototype name its classes "Harmony3Compat.X"; point those at this mod's classes.
        public static void PatchSaveLoading()
        {
            try
            {
                var target = AccessTools.Method(typeof(BackCompatibility), nameof(BackCompatibility.GetBackCompatibleType));
                if (target == null) return;
                new Harmony(HR.HarmonyId).Patch(target, prefix: new HarmonyMethod(typeof(PrototypeMigration), nameof(TypePrefix)));
            }
            catch (Exception e) { Log.Warning(HR.Tag + "saves from the prototype will lose their HR data: " + HR.Reason(e)); }
        }

        static bool TypePrefix(string providedClassName, ref Type __result)
        {
            if (providedClassName == null || !providedClassName.StartsWith(OldNamespace)) return true;
            Type t = typeof(HumanResourcesMod).Assembly.GetType("SloppyHR." + providedClassName.Substring(OldNamespace.Length), false);
            if (t == null) return true;
            __result = t;
            return false;
        }
    }
}
