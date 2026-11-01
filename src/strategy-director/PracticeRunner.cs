using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StrategyDirectorMod
{
    /// <summary>
    /// Practice is short (12-20 minutes), and the game's AI wastes much of it: a staggered
    /// 15-110 s wait before the first run, 15-30 s in the garage between runs, and 3-5 lap
    /// runs, so a car manages about two runs. A director-run practice instead:
    ///   1. goes out straight away (cars a few seconds apart so they don't queue);
    ///   2. does short setup runs (out-lap, one flying lap, in) while the engineer still has
    ///      work to do, so every return is another round of driver feedback;
    ///   3. then does the game's normal longer runs to build tyre and setup knowledge.
    /// </summary>
    internal static class PracticeRunner
    {
        public const int MaxSetupRuns = 4;
        public const float SetupGoal = 0.15f; // sum of the three gaps; ~all Great
        public static readonly FieldInfo WaitDuration = AccessTools.Field(typeof(GaragePathState), "mWaitDuration");
        public static readonly Dictionary<int, int> SetupRuns = new Dictionary<int, int>();
        public static string SessionKey;

        public static bool Applies(RacingVehicle v)
        {
            return v != null && v.isPlayerDriver && Main.Enabled && Main.Settings.RunPractice
                && Game.instance.sessionManager.sessionType == SessionDetails.SessionType.Practice
                && RaceState.Director != null && RaceState.IsControlled(v);
        }

        public static void EnsureSession()
        {
            string key = "";
            try
            {
                key = Game.instance.sessionManager.eventDetails.eventDate.Ticks + ":" + Game.instance.sessionManager.eventDetails.currentSession.sessionNumber;
            }
            catch (Exception)
            {
            }
            if (key != SessionKey)
            {
                SessionKey = key;
                SetupRuns.Clear();
            }
        }

        /// <summary>Total gap to the driver's ideal setup, or -1 when the engineer can't act (no Auto Setup).</summary>
        public static float SetupGap(RacingVehicle v)
        {
            SessionSetup.SetupOutput ai = Engineer.AISetup.GetValue(v.setup) as SessionSetup.SetupOutput;
            if (!Engineer.IsSet(ai))
            {
                return -1f;
            }
            SessionSetup.SetupOutput opt = v.performance.setupPerformance.GetOptimalSetup().setupOutput;
            return Mathf.Abs(ai.aerodynamics - opt.aerodynamics) + Mathf.Abs(ai.speedBalance - opt.speedBalance) + Mathf.Abs(ai.handling - opt.handling);
        }
    }

    /// <summary>Cut the garage wait: out at once (team-mate a few seconds behind), back out quickly after a run.</summary>
    [HarmonyPatch(typeof(GaragePathState), "OnEnter")]
    internal static class PracticeGarageWaitPatch
    {
        private static void Postfix(GaragePathState __instance)
        {
            try
            {
                RacingVehicle v = __instance.racingVehicle;
                if (!PracticeRunner.Applies(v))
                {
                    return;
                }
                PracticeRunner.EnsureSession();
                float wait = v.timer.HasSetLapTime() ? 4f : 2f + 6f * (v.carID % 2);
                float current = (float)PracticeRunner.WaitDuration.GetValue(__instance);
                if (current > wait)
                {
                    PracticeRunner.WaitDuration.SetValue(__instance, wait);
                }
            }
            catch (Exception e)
            {
                Main.Error("Practice garage timing failed: " + e.Message);
            }
        }
    }

    /// <summary>Choose the run length when the car is sent out: short setup runs first, then normal runs.</summary>
    [HarmonyPatch(typeof(SessionStrategy), "SetAIFuelForPracticeAndQualifying")]
    internal static class PracticeRunLengthPatch
    {
        private static void Postfix(SessionStrategy __instance)
        {
            try
            {
                RacingVehicle v = Context.VehicleOf(__instance);
                if (!PracticeRunner.Applies(v))
                {
                    return;
                }
                PracticeRunner.EnsureSession();
                float gap = PracticeRunner.SetupGap(v);
                int runs;
                PracticeRunner.SetupRuns.TryGetValue(v.carID, out runs);
                if (gap >= 0f && gap > PracticeRunner.SetupGoal && runs < PracticeRunner.MaxSetupRuns)
                {
                    PracticeRunner.SetupRuns[v.carID] = runs + 1;
                    v.performance.fuel.SetFuelLevel(3);
                    __instance.SetOrderedLapCount(1); // one flying lap: the out-lap is not counted as a lap
                    Main.Log(string.Format("PRACTICE OUT: {0} — setup run {1}: out-lap, one flying lap, in, for feedback (setup cost ~{2:0.00}s/lap).",
                        RaceState.DriverName(v), runs + 1, Mathf.Clamp01(gap / 2f)));
                }
                else
                {
                    Main.Log(string.Format("PRACTICE OUT: {0} — long run for tyre and setup knowledge ({1}).",
                        RaceState.DriverName(v), gap >= 0f ? string.Format("setup cost ~{0:0.00}s/lap", Mathf.Clamp01(gap / 2f)) : "setup is yours"));
                }
            }
            catch (Exception e)
            {
                Main.Error("Practice run length failed: " + e.Message);
            }
        }
    }
}
