using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Data.ScriptableObject;
using Game.CompanyScripts;
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
        public const string Version = "1.0.0";
    }

    // Makes geothermal power a per-body choice: how geologically active each body really is.
    //
    // Vanilla gives the standard geothermal plant a "Geological Activity" (internalFlux) build
    // requirement of 0.1-1 that only Io meets, and the game's flux values are mostly a shared
    // default (Earth and Mars 0.08, every asteroid 0.25). Instead, a table in the config gives
    // each body's output share, based on real heat flow: Io high, Earth good, Mars and Venus
    // moderate, Luna low, geologically dead bodies none. Unlisted bodies can't build.
    //
    // The vanilla requirement check is answered from the table (so the vanilla "Cannot build:
    // Geological Activity too low" tooltip still shows), and output goes through the plants'
    // habitability efficiency multiplier, which vanilla multiplies into energy production.
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        // Static so any class in the mod can write to the BepInEx log via Plugin.Log.
        internal static ManualLogSource Log;

        internal static ConfigEntry<string> FacilityIds;
        internal static ConfigEntry<string> BodyOutput;
        internal static ConfigEntry<bool> LogBodies;

        internal static HashSet<string> Ids = new HashSet<string>();

        private static readonly Dictionary<string, double> _table = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            FacilityIds = Config.Bind("Geothermal", "FacilityIds", "build_power_geothermal,build_power_geothermal_big",
                "Facility IDs treated as geothermal plants (comma separated).");
            BodyOutput = Config.Bind("Geothermal", "BodyOutput",
                "IO=1, EARTH=0.65, ENCELADUS=0.45, EUROPA=0.45, VENUS=0.4, MARS=0.3, " +
                "LUNA=0.2, MERCURY=0.2, GANYMEDE=0.2, TITAN=0.2, TRITON=0.2, CERES=0.2, PLUTO=0.15",
                "Geothermal output share per body (NAME=share, comma separated; names as in Teddit, e.g. MARS). " +
                "0 or unlisted means geothermal plants can't be built there. Based on real heat flow: " +
                "Io's volcanism, Earth's plate tectonics, tidal heating on Europa and Enceladus, weak residual heat " +
                "on Mars, Venus and the Moon, possible cryovolcanism on Titan, Triton, Ceres and Pluto.");
            LogBodies = Config.Bind("Debug", "LogBodies", true,
                "On the first in-game month after loading, write where geothermal can be built and its output to the log.");

            Ids = new HashSet<string>(FacilityIds.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
            foreach (string pair in BodyOutput.Value.Split(','))
            {
                string[] kv = pair.Split('=');
                if (kv.Length == 2 && double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double share))
                {
                    _table[kv[0].Trim()] = Math.Max(0, Math.Min(1, share));
                }
                else if (pair.Trim().Length > 0)
                {
                    Log.LogWarning($"Ignoring BodyOutput entry '{pair.Trim()}' (use NAME=share).");
                }
            }

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
            Log.LogInfo($"Loaded, applied {applied} patches, geothermal table has {_table.Count} bodies.");
        }

        // Output share on this body; 0 means geothermal can't be built there.
        internal static double Output(ObjectInfo oi)
        {
            if (oi == null)
            {
                return 0;
            }
            if (_table.TryGetValue(oi.TrueObjectName ?? "", out double share) || _table.TryGetValue(oi.idTranslation ?? "", out share))
            {
                return share;
            }
            return 0;
        }
    }

    // Makes sure every geothermal plant has an internal flux build requirement, and remembers
    // those requirement objects so their check can be answered from the table. Building data is
    // shared for the whole session, so this runs on every scene load and handles each plant once.
    internal static class Requirements
    {
        private static readonly HashSet<FacilityBaseDescriptor> _done = new HashSet<FacilityBaseDescriptor>();

        internal static readonly HashSet<CanBuildParameter.TerraformParameterOneCanBuild> Geothermal =
            new HashSet<CanBuildParameter.TerraformParameterOneCanBuild>();

        private const Objective.ChangeHabitabilityParametersObjectiveData.WhatHabitabilityParameterToChange InternalFlux =
            Objective.ChangeHabitabilityParametersObjectiveData.WhatHabitabilityParameterToChange.InternalFlux;

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
                    var cbp = fbd.CanBuildParameter;
                    if (cbp == null)
                    {
                        Plugin.Log.LogWarning($"{fbd.ID}: no build parameters; it keeps vanilla rules.");
                        continue;
                    }
                    cbp.terraformParameterCanBuild ??= new CanBuildParameter.TerraformParameterCanBuild();
                    var list = cbp.terraformParameterCanBuild.list;
                    var req = list.FirstOrDefault(r => r.terraformParameterSettingsCanBuild == InternalFlux);
                    if (req == null)
                    {
                        req = new CanBuildParameter.TerraformParameterOneCanBuild
                        {
                            terraformParameterSettingsCanBuild = InternalFlux,
                            min = 0,
                            max = double.MaxValue,
                        };
                        list.Add(req);
                        Plugin.Log.LogInfo($"{fbd.ID}: added a Geological Activity build requirement (answered from the table).");
                    }
                    else
                    {
                        Plugin.Log.LogInfo($"{fbd.ID}: vanilla Geological Activity requirement {req.min}-{req.max} now answered from the table.");
                    }
                    Geothermal.Add(req);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Setting up geothermal build requirements failed: " + e);
            }
        }
    }

    // The geothermal plants' Geological Activity check: buildable where the table gives output.
    [HarmonyPatch(typeof(CanBuildParameter.TerraformParameterOneCanBuild), nameof(CanBuildParameter.TerraformParameterOneCanBuild.Check))]
    internal static class BuildCheck
    {
        private static void Postfix(CanBuildParameter.TerraformParameterOneCanBuild __instance, ObjectInfo objectInfo,
            ref CanBuildParameter.TerraformParameterOneCanBuild.CheckResult __result)
        {
            if (Requirements.Geothermal.Contains(__instance))
            {
                __result = Plugin.Output(objectInfo) > 0
                    ? CanBuildParameter.TerraformParameterOneCanBuild.CheckResult.Ok
                    : CanBuildParameter.TerraformParameterOneCanBuild.CheckResult.TooLow;
            }
        }
    }

    // Plant output = the body's table share, through the habitability efficiency multiplier.
    [HarmonyPatch(typeof(Facility), nameof(Facility.UpdateHabitabilityEfficiencyMultiplier))]
    internal static class OutputShare
    {
        private static readonly AccessTools.FieldRef<Facility, double> Multiplier =
            AccessTools.FieldRefAccess<Facility, double>("habitabilityEfficiencyMultiplier");

        private static void Postfix(Facility __instance)
        {
            if (__instance?.facilityDescriptor != null && Plugin.Ids.Contains(__instance.facilityDescriptor.ID))
            {
                Multiplier(__instance) = Plugin.Output(__instance.ObjectInfoData?.ObjectInfo);
            }
        }
    }

    // Once per session, on the first in-game month: where geothermal can be built, and how well.
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
                    .Where(oi => oi != null && Plugin.Output(oi) > 0)
                    .Select(oi => $"{oi.TrueObjectName} {Plugin.Output(oi):P0}")
                    .ToList();
                Plugin.Log.LogInfo($"Geothermal can be built on {rows.Count} bodies: {string.Join(", ", rows)}.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Body report failed: " + e);
            }
        }
    }
}
