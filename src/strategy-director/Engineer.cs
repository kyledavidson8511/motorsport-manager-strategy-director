using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StrategyDirectorMod
{
    /// <summary>
    /// Setup work between practice runs, the way Kyle does it by hand: run, come in, listen to
    /// the driver, adjust. Auto Setup gives every car (AI included) a mechanic-quality baseline
    /// and then freezes it; this only touches the player's cars, only in practice, and only
    /// between runs, so the AI keeps Auto Setup's realistic level. Each return to the garage
    /// closes part of the gap to the driver's ideal setup on all three axes; how much depends
    /// on the driver's feedback and the mechanic. Qualifying and the race then load the best
    /// practice setup, as Auto Setup already does.
    /// </summary>
    internal static class Engineer
    {
        public static readonly FieldInfo AISetup = AccessTools.Field(typeof(SessionSetup), "mAISetupOutput");
        public static readonly FieldInfo PerfSetup = AccessTools.Field(typeof(SetupPerformance), "mSetupOutput");
        private static readonly System.Random sRandom = new System.Random();

        public static bool IsSet(SessionSetup.SetupOutput o)
        {
            return o != null && (o.aerodynamics != 0f || o.handling != 0f || o.speedBalance != 0f);
        }

        /// <summary>Share of the remaining gap closed per run: 0.40-0.80 from feedback and mechanic.</summary>
        public static float StepFor(RacingVehicle v)
        {
            float fb = 0.5f, mech = 0.5f;
            try { fb = Mathf.Clamp01(v.driver.GetDriverStats().feedback / 20f); } catch (Exception) { }
            try
            {
                Mechanic m = v.driver.contract.GetTeam().GetMechanicOfDriver(v.driver);
                if (m != null) mech = Mathf.Clamp01(m.stats.GetUnitAverage());
            }
            catch (Exception) { }
            float noise = (float)(sRandom.NextDouble() - 0.5) * 0.1f;
            return Mathf.Clamp(0.4f + 0.25f * fb + 0.15f * mech + noise, 0.3f, 0.85f);
        }

        public static string Opinion(float delta)
        {
            delta = Mathf.Abs(delta);
            if (delta < 0.03f) return "Excellent";
            if (delta < 0.1f) return "Great";
            if (delta < 0.2f) return "Good";
            if (delta < 0.3f) return "OK";
            if (delta < 0.5f) return "Poor";
            return "Very poor";
        }

        public static float Nudge(float current, float optimal, float step)
        {
            float d = (current - optimal) * (1f - step);
            d += (float)(sRandom.NextDouble() - 0.5) * 0.02f; // the driver's read is never perfect
            return optimal + d;
        }
    }

    internal static class EngineerWork
    {
        /// <summary>Per car: garage-return adjustments made since the last recorded stint.</summary>
        public static readonly Dictionary<int, int> GarageAdjusts = new Dictionary<int, int>();

        /// <summary>One round of driver feedback and setup changes. Returns false if nothing to do.</summary>
        public static bool Adjust(RacingVehicle v, string when)
        {
            SessionSetup.SetupOutput ai = Engineer.AISetup.GetValue(v.setup) as SessionSetup.SetupOutput;
            if (!Engineer.IsSet(ai))
            {
                return false; // Auto Setup isn't driving this car's setup; sliders are the player's
            }
            SessionSetup.SetupOutput opt = v.performance.setupPerformance.GetOptimalSetup().setupOutput;
            float before = Mathf.Abs(ai.aerodynamics - opt.aerodynamics) + Mathf.Abs(ai.speedBalance - opt.speedBalance) + Mathf.Abs(ai.handling - opt.handling);
            if (before < 0.02f)
            {
                return false;
            }
            float step = Engineer.StepFor(v);
            ai.aerodynamics = Engineer.Nudge(ai.aerodynamics, opt.aerodynamics, step);
            ai.speedBalance = Engineer.Nudge(ai.speedBalance, opt.speedBalance, step);
            ai.handling = Engineer.Nudge(ai.handling, opt.handling, step);
            SessionSetup.SetupOutput perf = Engineer.PerfSetup.GetValue(v.performance.setupPerformance) as SessionSetup.SetupOutput;
            if (perf != null)
            {
                perf.aerodynamics = ai.aerodynamics;
                perf.speedBalance = ai.speedBalance;
                perf.handling = ai.handling;
            }
            float da = ai.aerodynamics - opt.aerodynamics, ds = ai.speedBalance - opt.speedBalance, dh = ai.handling - opt.handling;
            float after = Mathf.Abs(da) + Mathf.Abs(ds) + Mathf.Abs(dh);
            Main.Log(string.Format("SETUP: {0}, {1} — engineer adjusts: downforce {2}, speed balance {3}, handling {4}; setup cost ~{5:0.00}s/lap (was {6:0.00}).",
                RaceState.DriverName(v), when, Engineer.Opinion(da), Engineer.Opinion(ds), Engineer.Opinion(dh),
                Mathf.Clamp01(after / 2f), Mathf.Clamp01(before / 2f)));
            return true;
        }

        public static bool Eligible(RacingVehicle v)
        {
            return Main.Enabled && Main.Settings.RefineSetups && v != null && v.isPlayerDriver
                && Game.instance.sessionManager.sessionType == SessionDetails.SessionType.Practice;
        }
    }

    /// <summary>Live practice: every time the car comes back to the garage, the driver debriefs and the engineer adjusts.</summary>
    [HarmonyPatch(typeof(GaragePathState), "OnEnter")]
    internal static class SetupGarageDebriefPatch
    {
        private static void Postfix(GaragePathState __instance)
        {
            try
            {
                RacingVehicle v = __instance.racingVehicle;
                if (!EngineerWork.Eligible(v) || v.timer.lapData == null || v.timer.lapData.Count == 0)
                {
                    return; // no laps yet: nothing to debrief
                }
                if (EngineerWork.Adjust(v, "debrief after a run"))
                {
                    int n;
                    EngineerWork.GarageAdjusts.TryGetValue(v.carID, out n);
                    EngineerWork.GarageAdjusts[v.carID] = n + 1;
                }
            }
            catch (Exception e)
            {
                Main.Error("Setup debrief failed: " + e.Message);
            }
        }
    }

    /// <summary>Simulated practice has no garage visits, only stints: adjust between simulated stints.</summary>
    [HarmonyPatch(typeof(PersistentEventData), "StartNewStint")]
    internal static class SetupEngineerPatch
    {
        private static void Prefix(RacingVehicle inVehicle)
        {
            try
            {
                if (!EngineerWork.Eligible(inVehicle))
                {
                    return;
                }
                int garage;
                if (EngineerWork.GarageAdjusts.TryGetValue(inVehicle.carID, out garage) && garage > 0)
                {
                    EngineerWork.GarageAdjusts[inVehicle.carID] = 0; // already debriefed in the garage
                    return;
                }
                List<SetupStintData> stints = Game.instance.persistentEventData.GetSetupStintData(inVehicle);
                if (stints == null || stints.Count == 0)
                {
                    return; // first run is the baseline
                }
                EngineerWork.Adjust(inVehicle, "run " + (stints.Count + 1));
            }
            catch (Exception e)
            {
                Main.Error("Setup engineer failed: " + e.Message);
            }
        }
    }
}
