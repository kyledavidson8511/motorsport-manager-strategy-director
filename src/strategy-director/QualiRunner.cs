using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StrategyDirectorMod
{
    /// <summary>
    /// Qualifying the way a pit wall does it (0.4.0):
    ///   1. Banker: out within a few seconds of the green light (team-mate ~10 s behind), one
    ///      flying lap, in. A time is on the board early, whatever happens later.
    ///   2. Final run: wait in the garage, then leave so the out-lap ends with ~20-30 s still on
    ///      the clock. The flying lap then starts before the flag and counts, on the best track.
    /// The game's own wait for player cars leaves with only about one lap plus 20-40 s to go, so
    /// the out-lap is still running at the flag and gets turned into an in-lap with no time.
    /// The out-lap doesn't count as a lap (SessionTimer.ShouldRegisterLapTime), so "1 lap" is
    /// one flying lap. Knockout formats get the same two runs per session; the game's
    /// "safely through, stay in" check (ShouldSendOutForQualifying) still applies.
    /// </summary>
    internal static class QualiRunner
    {
        public const float OutLapFactor = 1.25f;   // out-lap vs a flying lap
        public const float PitExitSeconds = 10f;  // garage to the pit exit at the limiter
        public const float BaseMargin = 22f;      // flying lap starts with ~22 s left (car 1)
        public const float TeamMateGap = 9f;      // car 2 leaves this much earlier: clean air, ~31 s left
        public const float BankerGap = 10f;       // banker: car 2 this far behind car 1

        public static string SessionKey;
        public static readonly HashSet<int> FinalSent = new HashSet<int>();
        public static readonly Dictionary<int, string> RunKind = new Dictionary<int, string>();
        public static readonly Dictionary<int, int> LapsAtSendOut = new Dictionary<int, int>();
        public static readonly Dictionary<int, float> TimeAtSendOut = new Dictionary<int, float>();

        public static bool Applies(RacingVehicle v)
        {
            try
            {
                return v != null && v.isPlayerDriver && Main.Enabled && Main.Settings.RunQualifying && Main.Settings.TimedQualifyingRuns
                    && Game.instance.sessionManager.sessionType == SessionDetails.SessionType.Qualifying
                    && v.championship.rules.gridSetup != ChampionshipRules.GridSetup.AverageLap
                    && RaceState.Director != null && RaceState.IsControlled(v);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void EnsureSession()
        {
            string key = "";
            try
            {
                key = Game.instance.sessionManager.eventDetails.eventDate.Ticks + ":" + Game.instance.sessionManager.eventDetails.currentSession.sessionNumber
                    + ":" + Game.instance.sessionManager.eventDetails.currentSession.sessionType;
            }
            catch (Exception)
            {
            }
            if (key != SessionKey)
            {
                SessionKey = key;
                FinalSent.Clear();
                RunKind.Clear();
                LapsAtSendOut.Clear();
                TimeAtSendOut.Clear();
            }
        }

        /// <summary>A flying lap for this car: its own best, else the session's best, else the game's estimate.</summary>
        public static float LapEstimate(RacingVehicle v)
        {
            float own = v.timer.GetFastestLapTime();
            if (own > 1f)
            {
                return own;
            }
            try
            {
                SessionTimer.LapData best = Game.instance.sessionManager.sessionFastestLapData.GetFastestLap(v.championship);
                if (best != null && best.time > 1f)
                {
                    return best.time * 1.02f;
                }
            }
            catch (Exception)
            {
            }
            try
            {
                SessionManager sm = Game.instance.sessionManager;
                float est = (float)SimulationLapTimeModel.EstimateLapTime(sm.raceDirector.highestCarStats, sm.circuit).TotalSeconds;
                if (est > 1f)
                {
                    return est * 1.03f;
                }
            }
            catch (Exception)
            {
            }
            return 100f;
        }

        public static float OutLap(float lap)
        {
            return OutLapFactor * lap + PitExitSeconds;
        }

        /// <summary>Session time left when the car should leave the garage for its final run.</summary>
        public static float FinalLeaveAt(RacingVehicle v, float lap)
        {
            return OutLap(lap) + BaseMargin + TeamMateGap * Math.Max(0, RaceState.CarIndex(v));
        }

        /// <summary>Time a whole banker run takes: out-lap, flying lap, in-lap and the garage turnaround.</summary>
        public static float BankerCycle(float lap)
        {
            return OutLap(lap) + lap + 1.2f * lap + 15f;
        }

        public static string Clock(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int s = Mathf.RoundToInt(seconds);
            return string.Format("{0}:{1:00}", s / 60, s % 60);
        }

        public static bool IsFinal(RacingVehicle v)
        {
            return v.timer.HasSetLapTime();
        }
    }

    /// <summary>Garage wait in qualifying: banker straight away, final timed to start its flying lap before the flag.</summary>
    [HarmonyPatch(typeof(GaragePathState), "OnEnter")]
    internal static class QualiGarageWaitPatch
    {
        private static void Postfix(GaragePathState __instance)
        {
            try
            {
                RacingVehicle v = __instance.racingVehicle;
                if (!QualiRunner.Applies(v))
                {
                    return;
                }
                QualiRunner.EnsureSession();
                SessionManager sm = Game.instance.sessionManager;
                float time = sm.time;
                float lap = QualiRunner.LapEstimate(v);
                int index = Math.Max(0, RaceState.CarIndex(v));

                // Back from a run: report it.
                int before;
                if (QualiRunner.LapsAtSendOut.TryGetValue(v.carID, out before))
                {
                    QualiRunner.LapsAtSendOut.Remove(v.carID);
                    string kind;
                    QualiRunner.RunKind.TryGetValue(v.carID, out kind);
                    int laps = v.timer.lapData.Count;
                    if (laps > before && laps > 0)
                    {
                        SessionTimer.LapData last = v.timer.lapData[laps - 1];
                        Main.Log(string.Format("QUALI RESULT: {0} — {1}: {2:0.000}s, P{3} after the run (best {4:0.000}s, {5} left).",
                            RaceState.DriverName(v), kind ?? "run", last.time, v.standingsPosition, v.timer.GetFastestLapTime(), QualiRunner.Clock(time)));
                    }
                    else
                    {
                        float leftAt;
                        bool aborted = QualiRunner.TimeAtSendOut.TryGetValue(v.carID, out leftAt) && leftAt - time < 0.6f * lap;
                        if (aborted && kind == "final")
                        {
                            // Turned straight back in the pit lane (the game's stale "return to garage"
                            // order): the final run never happened, so it's still owed.
                            QualiRunner.FinalSent.Remove(v.carID);
                        }
                        Main.Log(string.Format(aborted
                            ? "QUALI RESULT: {0} — {1}: turned back in the pit lane after {4:0}s (no lap started); going again (P{2}, {3} left)."
                            : "QUALI RESULT: {0} — {1}: no time set (P{2}, {3} left).",
                            RaceState.DriverName(v), kind ?? "run", v.standingsPosition, QualiRunner.Clock(time), leftAt - time));
                    }
                    QualiRunner.TimeAtSendOut.Remove(v.carID);
                }

                float wait;
                if (!v.timer.HasSetLapTime())
                {
                    // Banker (or a retry if the banker didn't count): go now, team-mate a few seconds behind.
                    wait = 3f + QualiRunner.BankerGap * index;
                }
                else if (QualiRunner.FinalSent.Contains(v.carID))
                {
                    wait = float.MaxValue; // final run done; nothing more to gain
                }
                else
                {
                    float leaveAt = QualiRunner.FinalLeaveAt(v, lap);
                    if (time > leaveAt + 4f)
                    {
                        wait = time - leaveAt;
                    }
                    else if (time > QualiRunner.OutLap(lap) + 5f)
                    {
                        wait = 3f; // tight: go now, the flying lap still starts before the flag
                    }
                    else
                    {
                        wait = float.MaxValue; // the out-lap couldn't finish in time
                        if (QualiRunner.FinalSent.Add(v.carID))
                        {
                            Main.Log(string.Format("QUALI: {0} — only {1} left, not enough for an out-lap (~{2:0}s); staying in.",
                                RaceState.DriverName(v), QualiRunner.Clock(time), QualiRunner.OutLap(lap)));
                        }
                    }
                }
                PracticeRunner.WaitDuration.SetValue(__instance, wait);
            }
            catch (Exception e)
            {
                Main.Error("Qualifying garage timing failed: " + e.Message);
            }
        }
    }


    /// <summary>When the car is sent out in qualifying: one flying lap, log which run it is.</summary>
    [HarmonyPatch(typeof(SessionStrategy), "SetAIFuelForPracticeAndQualifying")]
    internal static class QualiRunLengthPatch
    {
        private static void Postfix(SessionStrategy __instance)
        {
            try
            {
                RacingVehicle v = Context.VehicleOf(__instance);
                if (!QualiRunner.Applies(v))
                {
                    return;
                }
                QualiRunner.EnsureSession();
                SessionManager sm = Game.instance.sessionManager;
                float time = sm.time;
                float lap = QualiRunner.LapEstimate(v);
                bool final = QualiRunner.IsFinal(v);
                QualiRunner.LapsAtSendOut[v.carID] = v.timer.lapData.Count;
                QualiRunner.TimeAtSendOut[v.carID] = time;
                v.performance.fuel.SetFuelLevel(3);
                __instance.SetOrderedLapCount(1);
                TyreSet t = v.setup.tyreSet;
                string tyre = t == null ? "?" : string.Format("{0} {1}", t.GetCondition() >= 0.999f ? "fresh" : string.Format("used {0:0%}", t.GetCondition()), t.GetCompound());
                if (final)
                {
                    QualiRunner.FinalSent.Add(v.carID);
                    QualiRunner.RunKind[v.carID] = "final";
                    float flyingAt = time - QualiRunner.OutLap(lap);
                    Main.Log(string.Format("QUALI OUT: {0} — final run on {1}, leaving with {2} left (flying lap starts ~{3}).",
                        RaceState.DriverName(v), tyre, QualiRunner.Clock(time), QualiRunner.Clock(flyingAt)));
                }
                else
                {
                    QualiRunner.RunKind[v.carID] = "banker";
                    bool room = time > QualiRunner.BankerCycle(lap) + QualiRunner.FinalLeaveAt(v, lap);
                    Main.Log(string.Format("QUALI OUT: {0} — banker lap on {1}, {2} left{3}.",
                        RaceState.DriverName(v), tyre, QualiRunner.Clock(time),
                        room ? "; final run to follow" : "; may be the only run (short session)"));
                }
            }
            catch (Exception e)
            {
                Main.Error("Qualifying run length failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// The "double clutch" (0.4.1). While a player car sits in the garage after a run, the game's
    /// player-car logic (SessionStrategy.SimulationUpdate, NoActionRequired) sees the ordered laps
    /// as done and sets ReturningToGarage. The director then sends the car out, but that stale
    /// order is never cleared, so at the end of the pit-lane path the car turns straight back
    /// into the garage. Clear it at send-out for director-run cars in practice and qualifying.
    /// </summary>
    [HarmonyPatch(typeof(SessionStrategy), "SendOutOnTrack")]
    internal static class SendOutStaleReturnPatch
    {
        private static void Prefix(SessionStrategy __instance)
        {
            try
            {
                RacingVehicle v = Context.VehicleOf(__instance);
                if (v == null || !v.isPlayerDriver || !Main.Enabled || Main.Settings == null || !Main.Settings.FixDoubleClutch)
                {
                    return;
                }
                SessionDetails.SessionType t = Game.instance.sessionManager.sessionType;
                if (t == SessionDetails.SessionType.Race || RaceState.Director == null || !RaceState.IsControlled(v))
                {
                    return;
                }
                if (__instance.status == SessionStrategy.Status.ReturningToGarage)
                {
                    __instance.SetStatus(SessionStrategy.Status.NoActionRequired);
                    Main.Log(string.Format("GARAGE: {0} — cleared a stale return-to-garage order before sending the car out (no double clutch).", RaceState.DriverName(v)));
                }
            }
            catch (Exception e)
            {
                Main.Error("Send-out fix failed: " + e.Message);
            }
        }
    }

    /// <summary>Final runs finish after the flag, after which the cars never return to the garage: log their results at session end.</summary>
    [HarmonyPatch(typeof(RaceDirector), "OnSessionEnd")]
    internal static class QualiEndLogPatch
    {
        private static void Prefix()
        {
            try
            {
                if (Game.instance.sessionManager.sessionType != SessionDetails.SessionType.Qualifying || QualiRunner.LapsAtSendOut.Count == 0)
                {
                    return;
                }
                RacingVehicle[] cars = Game.instance.vehicleManager.GetPlayerVehicles();
                for (int i = 0; cars != null && i < cars.Length; i++)
                {
                    RacingVehicle v = cars[i];
                    int before;
                    if (v == null || !QualiRunner.LapsAtSendOut.TryGetValue(v.carID, out before))
                    {
                        continue;
                    }
                    string kind;
                    QualiRunner.RunKind.TryGetValue(v.carID, out kind);
                    int laps = v.timer.lapData.Count;
                    if (laps > before && laps > 0)
                    {
                        float t = v.timer.lapData[laps - 1].time;
                        Main.Log(string.Format("QUALI RESULT: {0} — {1}: {2:0.000}s at the flag, P{3} (best {4:0.000}s{5}).",
                            RaceState.DriverName(v), kind ?? "run", t, v.standingsPosition, v.timer.GetFastestLapTime(),
                            t <= v.timer.GetFastestLapTime() + 0.0005f ? ", improved" : ", no improvement"));
                    }
                    else
                    {
                        Main.Log(string.Format("QUALI RESULT: {0} — {1}: no time before the session ended, P{2}.", RaceState.DriverName(v), kind ?? "run", v.standingsPosition));
                    }
                }
                QualiRunner.LapsAtSendOut.Clear();
            }
            catch (Exception e)
            {
                Main.Error("Qualifying end log failed: " + e.Message);
            }
        }
    }
}
