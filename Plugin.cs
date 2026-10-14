using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Data.ScriptableObject;
using Game.ContractsObjectives;
using Game.Info;
using Game.ObjectInfoDataScripts;
using HarmonyLib;
using Manager;
using UnityEngine.SceneManagement;

namespace GeothermalTweaks
{
    // Mod identity. BepInEx uses the GUID to tell mods apart, so never change it after release.
    public static class PluginInfo
    {
        public const string Guid = "laserbees.solarexpanse.geothermaltweaks";
        public const string Name = "Geothermal Tweaks";
        public const string Version = "0.1.0";
    }

    // Lets geothermal power work on any body with some geological activity, with output on a
    // curve of the body's internal heat flux instead of all or nothing.
    //
    // Vanilla gives the geothermal plants a build requirement ("Geological Activity", the
    // body's internalFlux) that only Io meets. This lowers that minimum once at load (the
    // building data lives in memory, nothing is saved), and sets the plants' habitability
    // efficiency multiplier from internal flux, with a floor so it never reaches zero.
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        // Static so any class in the mod can write to the BepInEx log via Plugin.Log.
        internal static ManualLogSource Log;

        internal static ConfigEntry<string> FacilityIds;
        internal static ConfigEntry<float> MinFlux;
        internal static ConfigEntry<float> FullOutputFlux;
        internal static ConfigEntry<float> Exponent;
        internal static ConfigEntry<float> MinEfficiency;
        internal static ConfigEntry<bool> LogBodies;

        internal static HashSet<string> Ids = new HashSet<string>();

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            FacilityIds = Config.Bind("Geothermal", "FacilityIds", "build_power_geothermal,build_power_geothermal_big",
                "Facility IDs treated as geothermal plants (comma separated).");
            MinFlux = Config.Bind("Geothermal", "MinInternalFlux", 0.01f,
                "Lowest internal flux (\"Geological Activity\") a body needs to build geothermal plants. Vanilla only allows Io.");
            FullOutputFlux = Config.Bind("Geothermal", "FullOutputFlux", 0.4f,
                "Internal flux at which a plant reaches full output (Io is about 0.4).");
            Exponent = Config.Bind("Geothermal", "CurveExponent", 0.5f,
                "Output = (flux / FullOutputFlux) ^ exponent. 0.5 (square root) is gentle on low-flux bodies, 1 is linear.");
            MinEfficiency = Config.Bind("Geothermal", "MinEfficiency", 0.2f,
                "Output never drops below this share on a body that can build geothermal.");
            LogBodies = Config.Bind("Debug", "LogBodies", true,
                "On the first in-game month after loading, write every body's internal flux and geothermal output to the log.");

            Ids = new HashSet<string>(FacilityIds.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));

            // Applies every [HarmonyPatch] class in this mod, one at a time, so a patch that no
            // longer matches the game (e.g. after a game update) is logged instead of stopping the rest.
            _harmony = new Harmony(PluginInfo.Guid);
            int applied = 0;
            foreach (Type type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0)
                {
                    continue;
                }
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                    applied++;
                }
                catch (Exception e)
                {
                    Log.LogError("Patch " + type.Name + " failed: " + e);
                }
            }
            SceneManager.sceneLoaded += (scene, mode) => Requirements.Apply();
            Log.LogInfo("Loaded, applied " + applied + " patches.");
        }

        // Output share for a body's internal flux.
        internal static double Efficiency(double flux)
        {
            if (flux <= 0 || FullOutputFlux.Value <= 0)
            {
                return MinEfficiency.Value;
            }
            double e = Math.Pow(flux / FullOutputFlux.Value, Exponent.Value);
            return Math.Max(MinEfficiency.Value, Math.Min(1.0, e));
        }
    }

    // Lowers the plants' internal flux build minimum. Building data is shared and lives for the
    // whole session, so this runs whenever a scene loads and only changes each plant once.
    internal static class Requirements
    {
        private static readonly HashSet<FacilityBaseDescriptor> _done = new HashSet<FacilityBaseDescriptor>();

        internal static void Apply()
        {
            try
            {
                var all = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance?.AllFacility?.ListNotEmpty;
                if (all == null)
                {
                    return;
                }
                foreach (var fbd in all.Where(f => f != null && Plugin.Ids.Contains(f.ID) && !_done.Contains(f)))
                {
                    _done.Add(fbd);
                    var list = fbd.CanBuildParameter?.terraformParameterCanBuild?.list;
                    if (list == null || list.Count == 0)
                    {
                        Plugin.Log.LogInfo($"{fbd.ID}: no habitability build requirements.");
                        continue;
                    }
                    foreach (var req in list)
                    {
                        string before = $"{req.terraformParameterSettingsCanBuild} {req.min}–{req.max}";
                        if (req.terraformParameterSettingsCanBuild == Objective.ChangeHabitabilityParametersObjectiveData.WhatHabitabilityParameterToChange.InternalFlux
                            && req.min > Plugin.MinFlux.Value)
                        {
                            req.min = Plugin.MinFlux.Value;
                            Plugin.Log.LogInfo($"{fbd.ID}: build requirement {before} → min {req.min}.");
                        }
                        else
                        {
                            Plugin.Log.LogInfo($"{fbd.ID}: build requirement {before} (unchanged).");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Changing geothermal build requirements failed: " + e);
            }
        }
    }

    // Plant output follows the body's internal flux, through the facility's habitability
    // efficiency multiplier (which vanilla multiplies into energy production).
    [HarmonyPatch(typeof(Facility), nameof(Facility.UpdateHabitabilityEfficiencyMultiplier))]
    internal static class OutputCurve
    {
        private static readonly AccessTools.FieldRef<Facility, double> Multiplier =
            AccessTools.FieldRefAccess<Facility, double>("habitabilityEfficiencyMultiplier");

        private static void Postfix(Facility __instance)
        {
            if (__instance?.facilityDescriptor == null || !Plugin.Ids.Contains(__instance.facilityDescriptor.ID))
            {
                return;
            }
            var hp = __instance.ObjectInfoData?.ObjectInfo?.HabitabilityParameters;
            if (hp != null)
            {
                Multiplier(__instance) = Plugin.Efficiency(hp.internalFlux);
            }
        }
    }

    // Once per session, on the first in-game month: every body's internal flux and what a
    // geothermal plant would do there.
    [HarmonyPatch(typeof(ObjectInfo), "OnEachMonthUpdateHabitabilityAndVisualization")]
    internal static class BodyReport
    {
        private static bool _done;

        private static void Postfix()
        {
            if (_done || !Plugin.LogBodies.Value)
            {
                return;
            }
            _done = true;
            try
            {
                Requirements.Apply();
                var bodies = MonoBehaviourSingleton<ObjectInfoManager>.Instance?.allObjectInfos;
                if (bodies == null)
                {
                    return;
                }
                var rows = bodies
                    .Where(oi => oi != null && oi.HabitabilityParameters != null)
                    .Where(oi => oi.objectTypes.ToString() is var t && (t == "Planet" || t == "Moons" || t == "DwarfPlanet" || t == "Asteroid" || t == "Comet"))
                    .Select(oi => (name: oi.TrueObjectName, type: oi.objectTypes.ToString(), flux: oi.HabitabilityParameters.internalFlux))
                    .OrderByDescending(r => r.flux)
                    .ThenBy(r => r.name)
                    .ToList();
                var sb = new StringBuilder();
                sb.AppendLine($"Internal flux of {rows.Count} bodies (build minimum {Plugin.MinFlux.Value}, full output at {Plugin.FullOutputFlux.Value}):");
                foreach (var group in rows.GroupBy(r => Math.Round(r.flux, 4)))
                {
                    var names = group.Select(r => $"{r.name} ({r.type})").ToList();
                    string list = names.Count > 12 ? string.Join(", ", names.Take(12)) + $" … and {names.Count - 12} more" : string.Join(", ", names);
                    string output = group.Key >= Plugin.MinFlux.Value ? $"{Plugin.Efficiency(group.Key):P0} output" : "can't build";
                    sb.AppendLine($"  flux {group.Key,-7} {output,-12} {list}");
                }
                Plugin.Log.LogInfo(sb.ToString().TrimEnd());
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Body report failed: " + e);
            }
        }
    }
}
