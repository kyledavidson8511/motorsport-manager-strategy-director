using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace StrategyDirectorMod
{
    /// <summary>One car's dry-tyre plan, covering the dry window it was made for.</summary>
    internal class PlanInfo
    {
        public SessionStrategy.TyreOption[] options;
        public int baseStops;          // pit stops completed when the plan was made
        public bool resolvedOnGrid;    // plan[0] is the start tyre (else: the next stint)
        public int windowEndLap;       // lap the director expects slicks to stop working (-1 = race end)

        /// <summary>Index of the stint whose tyres are being chosen now.</summary>
        public int NextStintIndex(RacingVehicle v, bool onGrid)
        {
            if (onGrid) return 0;
            // Before the start (lap 0, no stops yet) the car is still choosing its start tyre.
            if (resolvedOnGrid && v.timer.stops == baseStops && Game.instance.sessionManager.lap == 0) return 0;
            return v.timer.stops - baseStops + (resolvedOnGrid ? 1 : 0);
        }

        /// <summary>Planned stops still to come after the stint the car is on now.</summary>
        public int StopsRemaining(RacingVehicle v)
        {
            int current = v.timer.stops - baseStops + (resolvedOnGrid ? 0 : -1);
            return options == null ? 0 : options.Length - 1 - current;
        }
    }

    /// <summary>
    /// Director state for the race weekend. Three scopes:
    ///   event   — the director and per-car plans (reloaded when the event changes)
    ///   session — who is in control right now (reset each practice/qualifying/race session)
    ///   race    — tyre plans, forecast read, calls (reset when the race session starts)
    /// </summary>
    internal static class RaceState
    {
        // event scope
        public static long EventKey;
        public static DateTime EventDate;
        public static StrategyDirector Director;

        // session scope
        public static string SessionKey;
        public static SessionDetails.SessionType SessionType;
        public static readonly HashSet<RacingVehicle> TakenBack = new HashSet<RacingVehicle>();
        public static readonly HashSet<RacingVehicle> RogueLogged = new HashSet<RacingVehicle>();
        public static readonly Dictionary<RacingVehicle, string> LastRunLogged = new Dictionary<RacingVehicle, string>();

        // race scope
        public static int ForecastErrorLaps;
        public static bool ForecastMissed;
        public static readonly Dictionary<RacingVehicle, PlanInfo> Plans = new Dictionary<RacingVehicle, PlanInfo>();
        public static readonly HashSet<RacingVehicle> WetLogged = new HashSet<RacingVehicle>();
        public static readonly HashSet<int> UndercutSkipLogged = new HashSet<int>();
        public static readonly HashSet<int> BringHomeLogged = new HashSet<int>();
        /// <summary>Per car: the lap a stretch decision was last logged.</summary>
        public static readonly Dictionary<int, int> StretchLogged = new Dictionary<int, int>();
        /// <summary>Per car: tyre condition at the end of the last lap, and the wear seen on that lap.</summary>
        public static readonly Dictionary<int, float> LastLapCondition = new Dictionary<int, float>();
        public static readonly Dictionary<int, float> LastLapWear = new Dictionary<int, float>();
        /// <summary>0.4.2: this stint's green-flag one-lap wear readings per player car (LastLapWear is their median).</summary>
        public static readonly Dictionary<int, List<float>> WearReadings = new Dictionary<int, List<float>>();
        /// <summary>Cars whose next reading spans part of a safety-car lap (skipped).</summary>
        public static readonly HashSet<int> WearSkipNext = new HashSet<int>();
        /// <summary>Per car: lap of the last stagger decision, whether it held, and pit-box wait seconds.</summary>
        public static readonly Dictionary<int, int> StaggerLap = new Dictionary<int, int>();
        public static readonly Dictionary<int, bool> StaggerHeld = new Dictionary<int, bool>();
        public static readonly Dictionary<int, float> BoxWait = new Dictionary<int, float>();
        public static readonly Dictionary<int, string> TyreLogged = new Dictionary<int, string>();
        public static readonly HashSet<RacingVehicle> UndercutPending = new HashSet<RacingVehicle>();
        public static readonly Dictionary<RacingVehicle, int> WeatherDecisionLap = new Dictionary<RacingVehicle, int>();
        public static readonly Dictionary<RacingVehicle, bool> WeatherDecision = new Dictionary<RacingVehicle, bool>();
        public static int Calls;
        public static int MissedCalls;

        /// <summary>The race that has already been summarised; stays blocked until a new event.</summary>
        public static long EndedKey;

        private static readonly System.Random sRandom = new System.Random();

        public static float Roll()
        {
            return (float)sRandom.NextDouble();
        }

        public static float Gaussian()
        {
            double u1 = 1.0 - sRandom.NextDouble();
            double u2 = sRandom.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
        }

        private static void ResetRace()
        {
            Plans.Clear();
            WetLogged.Clear();
            UndercutSkipLogged.Clear();
            BringHomeLogged.Clear();
            StretchLogged.Clear();
            LastLapCondition.Clear();
            LastLapWear.Clear();
            WearReadings.Clear();
            WearSkipNext.Clear();
            StaggerLap.Clear();
            StaggerHeld.Clear();
            BoxWait.Clear();
            TyreLogged.Clear();
            UndercutPending.Clear();
            WeatherDecisionLap.Clear();
            WeatherDecision.Clear();
            ForecastErrorLaps = 0;
            ForecastMissed = false;
            Calls = 0;
            MissedCalls = 0;
        }

        private static void ResetSession()
        {
            TakenBack.Clear();
            RogueLogged.Clear();
            LastRunLogged.Clear();
            ResetRace();
        }

        /// <summary>Full reset (end of race, or when the director leaves).</summary>
        public static void Reset()
        {
            EventKey = 0;
            EventDate = default(DateTime);
            Director = null;
            SessionKey = null;
            ResetSession();
        }

        /// <summary>
        /// True during a practice, qualifying or race session (from the garage or grid
        /// until the session ends). Loading screens and HQ don't count, and a race that
        /// has been summarised stays blocked until the next event.
        /// </summary>
        public static bool InDirectedSession()
        {
            try
            {
                SessionManager s = Game.instance.sessionManager;
                if (s == null || s.eventDetails == null)
                {
                    return false;
                }
                SessionDetails.SessionType t = s.sessionType;
                if (t != SessionDetails.SessionType.Practice && t != SessionDetails.SessionType.Qualifying && t != SessionDetails.SessionType.Race)
                {
                    return false;
                }
                GameState state = App.instance.gameStateManager.currentState;
                if (state == null || (state.group != GameState.Group.Event && state.group != GameState.Group.Simulation))
                {
                    return false;
                }
                return t != SessionDetails.SessionType.Race || s.eventDetails.eventDate.Ticks != EndedKey;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool InRace()
        {
            return InDirectedSession() && Game.instance.sessionManager.sessionType == SessionDetails.SessionType.Race;
        }

        /// <summary>Lazily set up event/session/race state (also covers loading a save mid-session).</summary>
        public static void Ensure()
        {
            if (!Main.Enabled || !InDirectedSession())
            {
                return;
            }
            SessionManager s = Game.instance.sessionManager;
            long eventKey = s.eventDetails.eventDate.Ticks;
            if (EventKey != eventKey)
            {
                Reset();
                EventKey = eventKey;
                EventDate = s.eventDetails.eventDate;
                Director = DirectorService.Current;
            }
            if (Director == null)
            {
                return;
            }
            string sessionKey = eventKey + ":" + s.sessionType + ":" + s.eventDetails.currentSession.sessionNumber;
            if (SessionKey == sessionKey)
            {
                return;
            }
            SessionKey = sessionKey;
            SessionType = s.sessionType;
            ResetSession();

            if (SessionType == SessionDetails.SessionType.Race)
            {
                StartRace(s);
            }
            else if (SessionAllowed())
            {
                Main.Log(string.Format("{0}: director {1} is running the session to the race plan ({2}).",
                    SessionName(s), Director.name, CarPlansText()));
            }
        }

        private static void StartRace(SessionManager s)
        {
            try
            {
                Team team = Director.contract.GetTeam();
                ForecastErrorLaps = Forecast.RollError(Director, team, s.lapCount);
                SessionWeatherDetails weather = s.currentSessionWeather;
                List<Forecast.Phase> phases = Forecast.Phases(weather, s.lapCount);
                bool anyWet = phases.Exists(ph => ph.tread != TyreSet.Tread.Slick);
                ForecastMissed = anyWet && Roll() < Forecast.MissChance(Director, team);
                string read = ForecastMissed ? "dry all race (missed the rain)" : Forecast.Describe(phases, s.lapCount, ForecastErrorLaps);
                string truth = Forecast.Describe(phases, s.lapCount, 0);
                if (read != null)
                {
                    Main.Log(string.Format("FORECAST: director reads {0}. Actual: {1}. (Logistics Centre level {2}, error {3:+0;-0;0} laps)",
                        read, truth, Forecast.WeatherHQLevel(team), ForecastMissed ? 0 : ForecastErrorLaps));
                }
            }
            catch (Exception e)
            {
                Main.Error("Forecast read failed: " + e);
            }
            Main.Log(string.Format("RACE START: director {0} ({1}, {2}); {3}.",
                Director.name, StrategyDirector.StarText(Director.stats.GetAbility()), StrategyDirector.SpecialtyName(Director.specialty), CarPlansText()));
        }

        public static string SessionName(SessionManager s)
        {
            try
            {
                return s.eventDetails.currentSession.GetSessionName().ToUpperInvariant();
            }
            catch (Exception)
            {
                return s.sessionType.ToString().ToUpperInvariant();
            }
        }

        public static string CarPlansText()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                RacingVehicle[] cars = Game.instance.vehicleManager.GetPlayerVehicles();
                for (int i = 0; cars != null && i < cars.Length; i++)
                {
                    if (cars[i] == null) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(DriverName(cars[i]) + ": " + StrategyDirector.PlanName(PlanFor(cars[i])));
                }
                if (sb.Length == 0 && Director != null)
                {
                    // Cars not on track yet (start of practice): name the team's drivers instead.
                    Team team = Director.contract.GetTeam();
                    for (int i = 0; i < 2; i++)
                    {
                        Driver drv = team.GetDriver(i);
                        if (drv == null) continue;
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(drv.name + ": " + StrategyDirector.PlanName(Director.PlanForDriver(drv, i, EventDate)));
                    }
                }
            }
            catch (Exception)
            {
            }
            return sb.Length == 0 ? "no cars yet" : sb.ToString();
        }

        /// <summary>Whether the director runs the current kind of session at all (settings).</summary>
        public static bool SessionAllowed()
        {
            switch (SessionType)
            {
                case SessionDetails.SessionType.Practice: return Main.Settings.RunPractice;
                case SessionDetails.SessionType.Qualifying: return Main.Settings.RunQualifying;
                default: return true;
            }
        }

        public static int CarIndex(RacingVehicle v)
        {
            try
            {
                RacingVehicle[] cars = Game.instance.vehicleManager.GetPlayerVehicles();
                for (int i = 0; cars != null && i < cars.Length; i++)
                {
                    if (cars[i] == v) return i;
                }
            }
            catch (Exception)
            {
            }
            return -1;
        }

        public static PlanType PlanFor(RacingVehicle v)
        {
            if (Director == null)
            {
                return PlanType.None;
            }
            return Director.PlanForDriver(v != null ? v.driver : null, CarIndex(v), EventDate);
        }

        public static bool IsRogue(RacingVehicle v)
        {
            try
            {
                return v.driver.personalityTraitController.UsesAIForStrategy(v);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>True when the director should be running this car right now.</summary>
        public static bool IsControlled(RacingVehicle v)
        {
            if (v == null || !v.isPlayerDriver)
            {
                return false;
            }
            Ensure();
            if (Director == null || !InDirectedSession() || !SessionAllowed() || TakenBack.Contains(v) || PlanFor(v) == PlanType.Manual)
            {
                return false;
            }
            if (SessionType == SessionDetails.SessionType.Race && IsRogue(v))
            {
                if (RogueLogged.Add(v))
                {
                    Main.Log(string.Format("ROGUE: {0} is ignoring the strategy (personality trait) and is freelancing on the game's own AI.", v.driver.name));
                }
                return false;
            }
            return true;
        }

        public static float PlanAggression(RacingVehicle v)
        {
            float a;
            switch (PlanFor(v))
            {
                case PlanType.FewestStops: a = 0.30f; break;
                case PlanType.Attack: a = 0.85f; break;
                case PlanType.Gamble: a = 0.60f; break;
                default: a = 0.55f; break;
            }
            if (Director != null)
            {
                if (Director.specialty == Specialty.Gambler) a += 0.10f;
                if (Director.specialty == Specialty.SafetyFirst) a -= 0.15f;
            }
            return Mathf.Clamp(a, 0.05f, 0.95f);
        }

        /// <summary>Chance the director makes the right call on the right lap.</summary>
        public static float ReactionChance(RacingVehicle v, bool weather)
        {
            if (Director == null)
            {
                return 0.5f;
            }
            DirectorRelationship rel = Director.GetRelationship(v.driver);
            if (rel != null && rel.InSync)
            {
                return 1f;
            }
            if (weather && Director.specialty == Specialty.WeatherReader)
            {
                return 0.98f;
            }
            float c = 0.5f + 0.45f * Mathf.Clamp01(Director.stats.reactions / 20f);
            if (rel != null && rel.HasTrust)
            {
                c += 0.05f;
            }
            return Mathf.Clamp01(c);
        }

        public static string DriverName(RacingVehicle v)
        {
            try
            {
                return v.driver.name;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>True when the team-mate is pitting, or already in the pit lane.</summary>
        public static bool TeamMateBusy(RacingVehicle v, out RacingVehicle mate)
        {
            mate = null;
            try
            {
                mate = Game.instance.vehicleManager.GetVehicleTeamMate(v);
                if (mate == null || mate.behaviourManager.isOutOfRace)
                {
                    return false;
                }
                if (mate.strategy.IsGoingToPit())
                {
                    return true;
                }
                PathController.Path path = mate.pathController.GetCurrentPath();
                return path != null && path.pathType != PathController.PathType.Track
                    && path.pathType != PathController.PathType.CrashLane
                    && path.pathType != PathController.PathType.RunWideLane
                    && path.pathType != PathController.PathType.CutCornerLane;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool UnderSafetyCar()
        {
            SessionManager.Flag f = Game.instance.sessionManager.flag;
            return f == SessionManager.Flag.SafetyCar || f == SessionManager.Flag.VirtualSafetyCar;
        }

        /// <summary>
        /// Laps the plan's remaining stints (from the next one) can cover on their best sets, scaled
        /// down if this car's tyres are wearing faster than the game's model says.
        /// </summary>
        public static float PlanReach(SessionStrategy strategy, RacingVehicle v, PlanInfo plan, out string sets)
        {
            sets = "";
            float lapLength = Math.Max(1f, v.pathController.GetPathData(PathController.PathType.Track).length);
            int next = plan.NextStintIndex(v, false);
            float reach = 0f;
            for (int si = Math.Max(0, next); si < plan.options.Length; si++)
            {
                TyreSet fresh = strategy.GetTyreInBestCondition(plan.options[si]);
                if (fresh == null) continue;
                reach += fresh.GetOptimalTyreDistance() / lapLength;
                sets += (sets.Length > 0 ? "+" : "") + fresh.GetCompound();
            }
            float seenWear;
            TyreSet cur = v.setup.currentSetup.tyreSet;
            float modelWear = cur != null ? lapLength / Math.Max(1f, cur.GetMaxDistance()) : 0f;
            if (modelWear > 0f && LastLapWear.TryGetValue(v.carID, out seenWear) && seenWear > modelWear)
            {
                reach *= modelWear / seenWear;
            }
            return reach;
        }

        /// <summary>Median of the readings (lower middle for an even count, so one spike never leads).</summary>
        public static float MedianWear(List<float> readings)
        {
            if (readings == null || readings.Count == 0)
            {
                return 0f;
            }
            List<float> sorted = new List<float>(readings);
            sorted.Sort();
            return sorted[(sorted.Count - 1) / 2];
        }

        /// <summary>Laps until the current set hits its cliff, from measured wear (10% margin).</summary>
        public static float LapsToCliff(RacingVehicle v)
        {
            TyreSet tyres = v.setup.currentSetup.tyreSet;
            float lapLength = Math.Max(1f, v.pathController.GetPathData(PathController.PathType.Track).length);
            float modelWear = lapLength / Math.Max(1f, tyres.GetMaxDistance());
            float seen;
            float wear = LastLapWear.TryGetValue(v.carID, out seen) && seen > 0f ? Math.Max(seen, 0.8f * modelWear) : modelWear;
            return (tyres.GetCondition() - tyres.GetCliffCondition()) / (wear * 1.1f);
        }

        public static string LapText()
        {
            try
            {
                SessionManager s = Game.instance.sessionManager;
                return "lap " + s.lap + "/" + s.lapCount;
            }
            catch (Exception)
            {
                return "lap ?";
            }
        }
    }

    /// <summary>
    /// Hands a single car to the game's AI while one of its strategy methods runs.
    /// The game asks "isUsingAIForPlayerDrivers" in these methods; that property reads
    /// the dev flag ConsoleCommands.useAIForDrivers, so we raise it for the duration
    /// of the call and put it back afterwards. We also swap the team's aggressiveness
    /// to the plan's value for the same window.
    /// </summary>
    internal static class Context
    {
        private static int sDepth;
        private static bool sSavedFlag;
        private static float sSavedAggression;
        private static TeamAIWeightings sWeights;

        public static bool Enter(RacingVehicle v)
        {
            if (!RaceState.IsControlled(v))
            {
                return false;
            }
            if (sDepth == 0)
            {
                sSavedFlag = ConsoleCommands.useAIForDrivers;
                sWeights = v.driver.contract.GetTeam().aiWeightings;
                if (sWeights != null)
                {
                    sSavedAggression = sWeights.mAggressiveness;
                    sWeights.mAggressiveness = RaceState.PlanAggression(v);
                }
                ConsoleCommands.useAIForDrivers = true;
            }
            sDepth++;
            return true;
        }

        public static void Exit(bool entered)
        {
            if (!entered)
            {
                return;
            }
            sDepth--;
            if (sDepth <= 0)
            {
                sDepth = 0;
                ConsoleCommands.useAIForDrivers = sSavedFlag;
                if (sWeights != null)
                {
                    sWeights.mAggressiveness = sSavedAggression;
                }
                sWeights = null;
            }
        }

        // Cached private-field accessors.
        private static readonly FieldInfo sStrategyVehicle = AccessTools.Field(typeof(SessionStrategy), "mVehicle");
        private static readonly FieldInfo sOrdersVehicle = AccessTools.Field(typeof(SessionAIOrderController), "mVehicle");

        public static RacingVehicle VehicleOf(SessionStrategy s)
        {
            return s == null ? null : sStrategyVehicle.GetValue(s) as RacingVehicle;
        }

        public static RacingVehicle VehicleOf(SessionAIOrderController c)
        {
            return c == null ? null : sOrdersVehicle.GetValue(c) as RacingVehicle;
        }

        /// <summary>Cheap pre-check so non-player cars pay nothing.</summary>
        public static bool Worth(RacingVehicle v)
        {
            return v != null && v.isPlayerDriver && Main.Enabled;
        }
    }

    // ----------------------------------------------------------------------
    // Context patches: each runs the original with the car handed to the AI.
    // ----------------------------------------------------------------------

    [HarmonyPatch(typeof(SessionStrategy), "OnEnterGate")]
    internal static class Ctx_OnEnterGate
    {
        private static void Prefix(SessionStrategy __instance, out bool __state)
        {
            RacingVehicle v = Context.VehicleOf(__instance);
            __state = Context.Worth(v) && Context.Enter(v);
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            Context.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(SessionStrategy), "OnExitPitlane")]
    internal static class Ctx_OnExitPitlane
    {
        private static void Prefix(SessionStrategy __instance, out bool __state)
        {
            RacingVehicle v = Context.VehicleOf(__instance);
            __state = Context.Worth(v) && Context.Enter(v);
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            Context.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(SessionAIOrderController), "UpdateAIOrders")]
    internal static class Ctx_UpdateAIOrders
    {
        private static void Prefix(SessionAIOrderController __instance, out bool __state)
        {
            RacingVehicle v = Context.VehicleOf(__instance);
            __state = Context.Worth(v) && Context.Enter(v);
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            Context.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(GridPathState), "OnEnter")]
    internal static class Ctx_GridOnEnter
    {
        private static void Prefix(GridPathState __instance, out bool __state)
        {
            RacingVehicle v = __instance.racingVehicle;
            __state = Context.Worth(v) && Context.Enter(v);
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            Context.Exit(__state);
            return __exception;
        }
    }

    /// <summary>Practice/qualifying: lets the game's AI send the car out of the garage on its own runs.</summary>
    [HarmonyPatch(typeof(GaragePathState), "SimulationUpdate")]
    internal static class Ctx_GarageUpdate
    {
        private static void Prefix(GaragePathState __instance, out bool __state)
        {
            RacingVehicle v = __instance.racingVehicle;
            __state = Context.Worth(v) && Context.Enter(v);
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            Context.Exit(__state);
            return __exception;
        }
    }

    /// <summary>Qualifying out-laps: the AI's engine/driving mode for the push lap.</summary>
    [HarmonyPatch(typeof(TrackPathState), "OnEnter")]
    internal static class Ctx_TrackOnEnter
    {
        private static void Prefix(TrackPathState __instance, out bool __state)
        {
            RacingVehicle v = __instance.racingVehicle;
            __state = Context.Worth(v) && Context.Enter(v);
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            Context.Exit(__state);
            return __exception;
        }
    }

    /// <summary>
    /// 1.1.0: safety car and VSC calls. The director makes one call per neutralised period, at the
    /// first chance, and refines the plan for the laps left instead of only asking "can the plan's
    /// next tyre reach the flag?".
    /// Milan 11/07, lap 5/14: both cars owed a stop on a Soft→Soft / Medium→Soft plan. A Soft (~8
    /// laps) couldn't reach the flag, so both stayed out; the top six boxed onto Mediums for the last
    /// 9 laps. On lap 7 Schmidt re-entered the safety-car train, the event fired again, Softs now
    /// reached, and he boxed from P1 as the safety car came in: P19, +24s.
    /// </summary>
    internal static class SafetyCarCall
    {
        /// <summary>Bumped whenever a safety car or VSC period starts.</summary>
        public static int Period;
        private static readonly Dictionary<int, int> sDecided = new Dictionary<int, int>();
        private static readonly FieldInfo sReason = AccessTools.Field(typeof(SessionStrategy), "mReasonForPreviousPit");

        public static void OnFlag(SessionManager.Flag from, SessionManager.Flag to)
        {
            bool wasNeutral = from == SessionManager.Flag.SafetyCar || from == SessionManager.Flag.VirtualSafetyCar;
            bool isNeutral = to == SessionManager.Flag.SafetyCar || to == SessionManager.Flag.VirtualSafetyCar;
            if (isNeutral && !wasNeutral)
            {
                Period++;
            }
        }

        /// <summary>True once the safety car has been told to come in (its last lap).</summary>
        public static bool SafetyCarComingIn()
        {
            try
            {
                Vehicle sv = Game.instance.vehicleManager.safetyVehicle;
                if (sv == null) return false;
                AISafetyCarBehaviour b = sv.behaviourManager.GetBehaviour<AISafetyCarBehaviour>();
                return b != null && b.headingToGarage;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Observed-vs-model wear scale for this car's tyres (1 = wearing as the game says).</summary>
        private static float WearScale(RacingVehicle v, float lapLength)
        {
            float seen;
            TyreSet cur = v.setup.currentSetup.tyreSet;
            float model = cur != null ? lapLength / Math.Max(1f, cur.GetMaxDistance()) : 0f;
            if (model > 0f && RaceState.LastLapWear.TryGetValue(v.carID, out seen) && seen > model) return model / seen;
            return 1f;
        }

        /// <summary>
        /// The fastest compound with a fresh set that runs the laps left on its own: of the sets that
        /// reach, the one with the shortest life (softest). Null when none reaches.
        /// </summary>
        public static TyreSet BestToFlag(SessionStrategy s, RacingVehicle v, int laps, out SessionStrategy.TyreOption option, out float reachLaps)
        {
            option = SessionStrategy.TyreOption.First;
            reachLaps = 0f;
            float lapLength = Math.Max(1f, v.pathController.GetPathData(PathController.PathType.Track).length);
            float scale = WearScale(v, lapLength);
            TyreSet best = null;
            foreach (SessionStrategy.TyreOption o in new[] { SessionStrategy.TyreOption.First, SessionStrategy.TyreOption.Second, SessionStrategy.TyreOption.Third })
            {
                TyreSet t = null;
                try { t = s.GetTyreInBestCondition(o); } catch (Exception) { }
                if (t == null || t.GetTread() != TyreSet.Tread.Slick) continue;
                float r = t.GetOptimalTyreDistance() / lapLength * scale;
                if (r < laps) continue;
                if (best == null || r < reachLaps)
                {
                    best = t;
                    option = o;
                    reachLaps = r;
                }
            }
            return best;
        }

        private static float CurrentReach(RacingVehicle v)
        {
            TyreSet cur = v.setup.currentSetup.tyreSet;
            if (cur == null) return 0f;
            float lapLength = Math.Max(1f, v.pathController.GetPathData(PathController.PathType.Track).length);
            return cur.GetOptimalTyreDistance() / lapLength * WearScale(v, lapLength);
        }

        private static string PlanText(SessionStrategy s, PlanInfo plan)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; plan != null && plan.options != null && i < plan.options.Length; i++)
            {
                TyreSet t = null;
                try { t = s.GetTyreInBestCondition(plan.options[i]); } catch (Exception) { }
                if (sb.Length > 0) sb.Append(" → ");
                sb.Append(t != null ? t.GetCompound().ToString() : plan.options[i].ToString());
            }
            return sb.ToString();
        }

        /// <summary>The call. Returns true when the game's own rule should run instead (no dry plan).</summary>
        public static bool Decide(SessionStrategy s, RacingVehicle v, bool vsc)
        {
            string label = vsc ? "VSC" : "SAFETY CAR";
            string who = RaceState.DriverName(v);
            if (v.pathState != null && v.pathState.IsInPitlaneArea())
            {
                // Already in the pit lane: decide when he's back out, if the period is still on.
                return false;
            }
            int seen;
            if (sDecided.TryGetValue(v.carID, out seen) && seen == Period)
            {
                // One call per period. Re-entering the train (after a pit stop, a lock-up, a run wide)
                // fires the event again, but the cheap moment was the first one.
                return false;
            }
            sDecided[v.carID] = Period;

            RaceState.Calls++;
            if (RaceState.Roll() > RaceState.ReactionChance(v, false))
            {
                RaceState.MissedCalls++;
                Main.Log(string.Format("{0}: {1}, {2} — the pit wall was slow to react; no stop.", label, who, RaceState.LapText()));
                return false;
            }
            // 1.1.1: checked before the plan, so a car already boxing is never credited to the call.
            if (v.strategy.IsGoingToPit())
            {
                Main.Log(string.Format("{0}: {1}, {2} — already coming in.", label, who, RaceState.LapText()));
                return false;
            }
            PlanInfo plan;
            bool hasPlan = RaceState.Plans.TryGetValue(v, out plan) && plan != null;
            bool gamble = RaceState.PlanFor(v) == PlanType.Gamble;
            if (!hasPlan || (!Main.Settings.PitUnderSafetyCar && !gamble))
            {
                return !vsc;
            }
            SessionManager sm = Game.instance.sessionManager;
            int lapsLeft = sm.GetLapsRemaining();
            // Plan the dry spell only, if rain is expected before the flag.
            int target = plan.windowEndLap > 0 ? Math.Min(lapsLeft, Math.Max(1, plan.windowEndLap - sm.lap)) : lapsLeft;
            if (lapsLeft <= 3)
            {
                Main.Log(string.Format("{0}: {1}, {2} — staying out: too few laps left.", label, who, RaceState.LapText()));
                return false;
            }
            if (!vsc && SafetyCarComingIn())
            {
                Main.Log(string.Format("{0}: {1}, {2} — staying out: the safety car comes in this lap, so a stop now costs nearly a full green-flag stop.", label, who, RaceState.LapText()));
                return false;
            }
            if (vsc)
            {
                float left = sm.raceDirector.crashDirector.virtualSafetyCarTimer;
                float lapTime = v.timer.averageLapTime > 1f ? v.timer.averageLapTime : 90f;
                float toPits = Mathf.Clamp01(1f - v.pathController.distanceAlongTrackPath01) * lapTime * 1.4f;
                if (toPits > left)
                {
                    Main.Log(string.Format("{0}: {1}, {2} — staying out: the VSC (~{3:0}s left) will be over before he reaches the pit entry (~{4:0}s).", label, who, RaceState.LapText(), left, toPits));
                    return false;
                }
            }

            int owed = plan.StopsRemaining(v);
            TyreSet tyres = v.setup.currentSetup.tyreSet;
            float cond = tyres != null ? tyres.GetCondition() : 0f;
            SessionStrategy.TyreOption opt;
            float reach;
            TyreSet toFlag = BestToFlag(s, v, target, out opt, out reach);
            string toWhat = target < lapsLeft ? "to the rain" : "to the flag";

            if (owed > 0 || gamble)
            {
                if (toFlag != null)
                {
                    // Refine: the stop still owed, taken now, on whatever runs to the end.
                    string before = PlanText(s, plan);
                    RaceState.Plans[v] = new PlanInfo { options = new[] { opt }, baseStops = v.timer.stops, resolvedOnGrid = false, windowEndLap = plan.windowEndLap };
                    Main.Log(string.Format("{0}: {1}, {2} — plan refined: was {3} ({4} stop(s) left); box now for {5} (~{6:0} laps) {7}, {8} to go (tyres {9:0%}).",
                        label, who, RaceState.LapText(), before, owed, toFlag.GetCompound(), reach, toWhat, target, cond));
                    Box(s, v);
                    return false;
                }
                string sets;
                float planReach = RaceState.PlanReach(s, v, plan, out sets);
                if (owed > 1 && planReach >= target)
                {
                    // Several stops still owed and no single set reaches: take one of them now.
                    Main.Log(string.Format("{0}: {1}, {2} — the plan still owes {3} stops; taking one now while it's cheap ({4} for {5} to go).",
                        label, who, RaceState.LapText(), owed, sets, target));
                    Box(s, v);
                    return false;
                }
                Main.Log(string.Format("{0}: {1}, {2} — staying out: no fresh set runs {3} laps {4}, so stopping now would cost a second stop (tyres {5:0%}).",
                    label, who, RaceState.LapText(), target, toWhat, cond));
                return false;
            }

            float curReach = CurrentReach(v);
            if (toFlag != null && (curReach < target || (target >= 5 && cond < 0.6f)))
            {
                RaceState.Plans[v] = new PlanInfo { options = new[] { opt }, baseStops = v.timer.stops, resolvedOnGrid = false, windowEndLap = plan.windowEndLap };
                Main.Log(string.Format("{0}: {1}, {2} — no stop owed, but tyres are {3:0%} (~{4:0} laps) with {5} to go; fresh {6} (~{7:0} laps) {8} while it's cheap.",
                    label, who, RaceState.LapText(), cond, curReach, target, toFlag.GetCompound(), reach, toWhat));
                Box(s, v);
                return false;
            }
            Main.Log(string.Format("{0}: {1}, {2} — staying out: no stop owed and tyres {3:0%} (~{4:0} laps) are fine for {5} to go.",
                label, who, RaceState.LapText(), cond, curReach, target));
            return false;
        }

        private static void Box(SessionStrategy s, RacingVehicle v)
        {
            bool entered = Context.Enter(v);
            try
            {
                // Reason first: PlanPitstop calls the car in straight away and the pit log reads it.
                sReason.SetValue(s, SessionStints.ReasonForStint.PitTyreChange);
                Traverse.Create(s).Method("PlanPitstop").GetValue();
                sReason.SetValue(s, SessionStints.ReasonForStint.PitTyreChange);
            }
            finally
            {
                Context.Exit(entered);
            }
        }
    }

    [HarmonyPatch(typeof(SessionManager), "SetDesiredFlag")]
    internal static class FlagPeriodPatch
    {
        private static void Prefix(SessionManager __instance, out SessionManager.Flag __state)
        {
            __state = __instance.flag;
        }

        private static void Postfix(SessionManager __instance, SessionManager.Flag __state)
        {
            try
            {
                SafetyCarCall.OnFlag(__state, __instance.flag);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Safety car: the game's event (fires when a car joins the safety-car train).</summary>
    [HarmonyPatch(typeof(SessionStrategy), "OnSafetyCarEvent")]
    internal static class Ctx_OnSafetyCarEvent
    {
        private static bool sRanOriginal;

        private static bool Prefix(SessionStrategy __instance, out bool __state)
        {
            __state = false;
            sRanOriginal = false;
            RacingVehicle v = Context.VehicleOf(__instance);
            if (!Context.Worth(v) || !RaceState.IsControlled(v))
            {
                return true;
            }
            try
            {
                if (SafetyCarCall.Decide(__instance, v, false))
                {
                    // No dry plan (wet running) or SC stops switched off: the game's own rule decides.
                    sRanOriginal = true;
                    __state = Context.Enter(v);
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                Main.Error("Safety car call failed: " + e);
                return true;
            }
        }

        private static void Postfix(SessionStrategy __instance)
        {
            RacingVehicle v = Context.VehicleOf(__instance);
            if (sRanOriginal && v != null && RaceState.Director != null && v.isPlayerDriver && v.strategy.IsGoingToPit() && RaceState.IsControlled(v))
            {
                Main.Log(string.Format("SAFETY CAR: {0}, {1} — director calls the car in.", RaceState.DriverName(v), RaceState.LapText()));
            }
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            Context.Exit(__state);
            return __exception;
        }
    }

    /// <summary>VSC: the game has no strategy event for it, so the director's call hooks the car joining the VSC.</summary>
    [HarmonyPatch(typeof(AISafetyFlagBehaviour), "OnEnter")]
    internal static class VscEnterPatch
    {
        private static void Postfix(AISafetyFlagBehaviour __instance)
        {
            try
            {
                if (Game.instance.sessionManager.flag != SessionManager.Flag.VirtualSafetyCar)
                {
                    return;
                }
                RacingVehicle v = Traverse.Create(__instance).Field("mRacingVehicle").GetValue<RacingVehicle>();
                if (v == null || !v.isPlayerDriver || !RaceState.InRace() || !Context.Worth(v) || !RaceState.IsControlled(v))
                {
                    return;
                }
                SafetyCarCall.Decide(v.strategy, v, true);
            }
            catch (Exception e)
            {
                Main.Error("VSC call failed: " + e);
            }
        }
    }

    // ----------------------------------------------------------------------
    // Plan levers
    // ----------------------------------------------------------------------

    /// <summary>Tyres: resolve the plan once per car, then fit the planned compound for each stint.</summary>
    [HarmonyPatch(typeof(SessionStrategy), "GetBestTyresForTime")]
    internal static class TyrePlanPatch
    {
        private static void Postfix(SessionStrategy __instance, ref TyreSet __result)
        {
            if (__result == null)
            {
                return;
            }
            RacingVehicle v = Context.VehicleOf(__instance);
            if (!Context.Worth(v) || !RaceState.IsControlled(v))
            {
                return;
            }
            try
            {
                if (RaceState.SessionType != SessionDetails.SessionType.Race)
                {
                    Weekend.ChooseSessionTyre(__instance, v, ref __result);
                    return;
                }
                if (__result.GetTread() != TyreSet.Tread.Slick)
                {
                    // Wet tyres are the game's call. Drop the dry plan; a fresh one is
                    // made for the next dry spell when slicks are wanted again.
                    if (RaceState.Plans.Remove(v) || RaceState.WetLogged.Add(v))
                    {
                        Main.Log(string.Format("WEATHER: {0}, {1} — on {2}; the dry plan will be redrawn when it dries.", RaceState.DriverName(v), RaceState.LapText(), Forecast.TreadName(__result.GetTread())));
                    }
                    return;
                }
                bool onGrid = v.pathState.currentState != null && v.pathState.currentState.stateType == PathStateManager.StateType.Grid;
                PlanInfo plan = PlanResolver.GetOrResolve(__instance, v, onGrid);
                if (plan == null)
                {
                    return;
                }
                int stint = plan.NextStintIndex(v, onGrid);
                if (stint == plan.options.Length && !onGrid
                    && v.pathState != null && !v.pathState.IsInPitlaneArea())
                {
                    // 1.0.2: the car is on its last planned stint and still on track; this is the game
                    // asking "what would I fit?" for a weather check, not a stop. Keep the plan.
                    // 1.1.0: any plan whose stops are all done (incl. one refined under a safety car).
                    return;
                }
                if (stint >= plan.options.Length)
                {
                    // Out of plan (the rain didn't come when expected, or an unplanned stop): redraw.
                    RaceState.Plans.Remove(v);
                    plan = PlanResolver.GetOrResolve(__instance, v, onGrid);
                    if (plan == null)
                    {
                        return;
                    }
                    stint = plan.NextStintIndex(v, onGrid);
                }
                if (stint >= 0 && stint < plan.options.Length)
                {
                    TyreSet planned = __instance.GetTyreInBestCondition(plan.options[stint]);
                    if (planned != null)
                    {
                        __result = planned;
                    }
                    string line = string.Format("TYRES: {0}, {1} — stint {2} of {3}: {4}{5}.",
                        RaceState.DriverName(v), RaceState.LapText(), stint + 1, plan.options.Length,
                        __result.GetCompound(), planned == null ? " (no " + plan.options[stint] + " set left; the game's pick)" : string.Format(" ({0:0%} set)", __result.GetCondition()));
                    string last;
                    string tyreKey = stint + ":" + __result.GetCompound();
                    if (!RaceState.TyreLogged.TryGetValue(v.carID, out last) || last != tyreKey)
                    {
                        RaceState.TyreLogged[v.carID] = tyreKey;
                        Main.Log(line);
                    }
                }
            }
            catch (Exception e)
            {
                Main.Error("Tyre plan failed: " + e);
            }
        }
    }

    internal static class PlanResolver
    {
        public static PlanInfo GetOrResolve(SessionStrategy strategy, RacingVehicle v, bool onGrid)
        {
            PlanInfo plan;
            if (RaceState.Plans.TryGetValue(v, out plan))
            {
                return plan;
            }
            int windowEnd;
            SessionStrategy.TyreOption[] options = Resolve(strategy, v, out windowEnd);
            // A plan made before the start (on the grid, or in the garage before the formation) has
            // the start tyre as stint 1. Treating a pre-grid plan as "next stint" put every stop one
            // stint behind (0.1.1-0.3.3): the start compound was fitted again and the last planned
            // compound never used.
            bool startPlan = onGrid || (Game.instance.sessionManager.lap == 0 && v.timer.stops == 0);
            // 1.0.2: a plan drawn out on track while the car already runs the plan's first compound
            // (after a weather or hand-back stop) starts with the tyres on the car. Treating them as
            // "next stint" left a phantom stop owed, and the safety-car rule boxed for it (Milan 11/05:
            // Schmidt pitted on 93% Softs from P1-pace to P16; Lara on 85% Softs). timer.stops only
            // counts a stop at pit exit, so plans drawn in the pitlane are left as they were.
            bool runningFirst = false;
            if (!startPlan && options != null && options.Length > 0 && v.pathState != null && !v.pathState.IsInPitlaneArea())
            {
                TyreSet cur = v.setup.currentSetup.tyreSet;
                if (cur != null && cur.GetTread() == TyreSet.Tread.Slick && strategy.GetOptionFromCompound(cur.GetCompound()) == options[0])
                {
                    runningFirst = true;
                    Main.Log(string.Format("PLAN: {0}, {1} — already on {2}; counting the tyres on the car as stint 1.",
                        RaceState.DriverName(v), RaceState.LapText(), cur.GetCompound()));
                }
            }
            plan = options == null ? null : new PlanInfo { options = options, baseStops = v.timer.stops, resolvedOnGrid = startPlan || runningFirst, windowEndLap = windowEnd };
            RaceState.Plans[v] = plan;
            return plan;
        }

        private class Candidate
        {
            public SessionStrategy.TyreOption[] options;
            public float cost;
            public float noisyCost;
            public float coverage;   // metres the option's tyre sets can cover
            public int Stints => options.Length;
        }

        private static SessionStrategy.TyreOption[] Resolve(SessionStrategy strategy, RacingVehicle v, out int windowEnd)
        {
            windowEnd = -1;
            TyreStrategyOption[] all = Traverse.Create(strategy).Field("mTyreStrategyOption").GetValue<TyreStrategyOption[]>();
            if (all == null || all.Length == 0)
            {
                return null;
            }
            SessionManager s = Game.instance.sessionManager;
            float lapLength = v.pathController.GetPathData(PathController.PathType.Track).length;
            int laps = Math.Max(1, s.GetLapsRemaining());
            string windowNote = "";
            // Plan only the dry spell the director *thinks* is coming (imperfect forecast).
            if (!RaceState.ForecastMissed)
            {
                int wet = Forecast.NextWetLap(s.currentSessionWeather, s.lap, s.lapCount);
                if (wet > 0)
                {
                    int predicted = Mathf.Clamp(wet + RaceState.ForecastErrorLaps, s.lap + 1, s.lapCount);
                    int dryLaps = Math.Max(1, predicted - Math.Max(1, s.lap));
                    if (dryLaps < laps)
                    {
                        laps = dryLaps;
                        windowEnd = predicted;
                        windowNote = string.Format(" for the dry spell to ~lap {0} (rain expected)", predicted);
                    }
                }
            }
            float distance = lapLength * laps;

            StrategyDirector d = RaceState.Director;
            float sigma = (1f - Mathf.Clamp01(d.stats.planning / 20f)) * 30f;
            if (d.specialty == Specialty.TyreWhisperer)
            {
                sigma *= 0.4f;
            }

            List<Candidate> valid = new List<Candidate>();
            List<Candidate> partial = new List<Candidate>();
            for (int i = 0; i < all.Length; i++)
            {
                TyreStrategyOption o = all[i];
                if (o == null)
                {
                    continue;
                }
                SessionStrategy.TyreOption[] opts = Traverse.Create(o).Field("mTyreOptions").GetValue<SessionStrategy.TyreOption[]>();
                if (opts == null || opts.Length == 0)
                {
                    continue;
                }
                o.CalculateEstimatedStrategy(distance);
                float coverage = 0f;
                TyreSet[] sets = Traverse.Create(o).Field("mTyreSets").GetValue<TyreSet[]>();
                for (int k = 0; sets != null && k < sets.Length; k++)
                {
                    if (sets[k] != null) coverage += sets[k].GetOptimalTyreDistance();
                }
                Candidate cand = new Candidate { options = opts, cost = o.timeCost, noisyCost = o.timeCost + RaceState.Gaussian() * sigma, coverage = coverage };
                if (o.isValidOption)
                {
                    valid.Add(cand);
                }
                else if (coverage > 0f)
                {
                    partial.Add(cand);
                }
            }
            if (valid.Count == 0)
            {
                // No combination of up to 3 stints reaches the flag (high wear, or sets used up in
                // practice). Plan the first stints in the spirit of the plan and extend at the stops.
                Candidate p = PickPartial(partial, RaceState.PlanFor(v));
                if (p == null)
                {
                    Main.Log("PLAN: " + RaceState.DriverName(v) + " — no tyre plan possible; the game's AI will choose tyres.");
                    return null;
                }
                Main.Log(string.Format("PLAN: {0}, {1} — {2}: no 3-stint plan reaches the flag ({3} laps); first stints {4} cover ~{5:0} laps, I'll extend it at the stops.",
                    RaceState.DriverName(v), RaceState.LapText(), StrategyDirector.PlanName(RaceState.PlanFor(v)), laps,
                    Describe(strategy, p.options), p.coverage / Math.Max(1f, lapLength)));
                return p.options;
            }

            // Never plan stints shorter than ~3 laps (a 1-lap dry window gets one stint, not a stop).
            List<Candidate> sensible = valid.FindAll(c => c.Stints == 1 || laps / (float)c.Stints >= 3f);
            if (sensible.Count > 0)
            {
                valid = sensible;
            }
            Candidate fastest = Best(valid, c => true);
            Candidate pick;
            switch (RaceState.PlanFor(v))
            {
                case PlanType.FewestStops:
                {
                    int minStints = int.MaxValue;
                    foreach (Candidate c in valid) minStints = Math.Min(minStints, c.Stints);
                    pick = Best(valid, c => c.Stints == minStints);
                    break;
                }
                case PlanType.Attack:
                    // An extra stop only pays if the stints stay a decent length; otherwise attack on softer rubber.
                    pick = Best(valid, c => c.Stints > fastest.Stints && laps / (float)c.Stints >= 4f)
                        ?? Best(valid, c => c.Stints == fastest.Stints && Count(c, SessionStrategy.TyreOption.First) > Count(fastest, SessionStrategy.TyreOption.First))
                        ?? fastest;
                    break;
                case PlanType.Gamble:
                {
                    SessionStrategy.TyreOption hardest = Game.instance.sessionManager.championship.rules.compoundsAvailable >= 3 ? SessionStrategy.TyreOption.Third : SessionStrategy.TyreOption.Second;
                    pick = Best(valid, c => c.Stints >= 2 && c.options[0] == hardest) ?? Best(valid, c => c.options[0] != SessionStrategy.TyreOption.First) ?? fastest;
                    break;
                }
                default:
                    pick = fastest;
                    break;
            }

            try
            {
                int slot = RaceState.CarIndex(v);
                PlanType bySlot = d.PlanForCar(slot, RaceState.EventDate);
                if (bySlot != RaceState.PlanFor(v))
                {
                    Main.Log(string.Format("PLAN CHECK: {0} — car slot {1} holds {2}, the driver's plan is {3}; using the driver's (0.4.2 fix).",
                        RaceState.DriverName(v), slot, StrategyDirector.PlanName(bySlot), StrategyDirector.PlanName(RaceState.PlanFor(v))));
                }
            }
            catch (Exception)
            {
            }
            float trueBest = float.MaxValue;
            foreach (Candidate c in valid) trueBest = Math.Min(trueBest, c.cost);
            Main.Log(string.Format("PLAN: {0}, {1} — {2}{3}: {4} ({5} stop{6}); est. {7:+0.0;-0.0;0.0}s vs the true best option.",
                RaceState.DriverName(v), RaceState.LapText(), StrategyDirector.PlanName(RaceState.PlanFor(v)), windowNote, Describe(strategy, pick.options),
                pick.Stints - 1, pick.Stints - 1 == 1 ? "" : "s", pick.cost - trueBest));
            return pick.options;
        }

        /// <summary>Best of the options that don't reach the flag, judged by what the plan cares about.</summary>
        private static Candidate PickPartial(List<Candidate> list, PlanType plan)
        {
            Candidate best = null;
            float bestScore = float.MaxValue;
            SessionStrategy.TyreOption hardest = Weekend.Hardest();
            foreach (Candidate c in list)
            {
                float perStint = c.coverage / Math.Max(1, c.Stints);
                float costPerMetre = c.noisyCost / Math.Max(1f, c.coverage);
                float score;
                switch (plan)
                {
                    case PlanType.FewestStops:
                        score = -perStint;                       // longest stints
                        break;
                    case PlanType.Gamble:
                        score = (c.options[0] == hardest ? 0f : 1e6f) - c.options.Length * 0f - perStint;
                        break;
                    case PlanType.Attack:
                        score = costPerMetre - 0.001f * Count(c, SessionStrategy.TyreOption.First);
                        break;
                    default:
                        score = costPerMetre;                    // quickest per metre covered
                        break;
                }
                if (best == null || score < bestScore)
                {
                    best = c;
                    bestScore = score;
                }
            }
            return best;
        }

        private static Candidate Best(List<Candidate> list, Predicate<Candidate> filter)
        {
            Candidate best = null;
            foreach (Candidate c in list)
            {
                if (filter(c) && (best == null || c.noisyCost < best.noisyCost))
                {
                    best = c;
                }
            }
            return best;
        }

        private static int Count(Candidate c, SessionStrategy.TyreOption o)
        {
            int n = 0;
            foreach (SessionStrategy.TyreOption x in c.options) if (x == o) n++;
            return n;
        }

        private static string Describe(SessionStrategy strategy, SessionStrategy.TyreOption[] opts)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < opts.Length; i++)
            {
                if (i > 0) sb.Append(" → ");
                try
                {
                    sb.Append(strategy.GetTyreCompound(opts[i]).ToString());
                }
                catch (Exception)
                {
                    sb.Append(opts[i].ToString());
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Weather: the call to change tread can come a lap or two late with a weaker director.</summary>
    [HarmonyPatch(typeof(SessionStrategy), "ShouldPitForDifferentTyreTread")]
    internal static class WeatherCallPatch
    {
        private static void Postfix(SessionStrategy __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }
            RacingVehicle v = Context.VehicleOf(__instance);
            if (!Context.Worth(v) || !RaceState.InRace() || !RaceState.IsControlled(v))
            {
                return;
            }
            try
            {
                int lap = Game.instance.sessionManager.lap;
                int decidedLap;
                bool decision;
                if (!RaceState.WeatherDecisionLap.TryGetValue(v, out decidedLap) || decidedLap != lap)
                {
                    decision = RaceState.Roll() <= RaceState.ReactionChance(v, true);
                    RaceState.WeatherDecisionLap[v] = lap;
                    RaceState.WeatherDecision[v] = decision;
                    RaceState.Calls++;
                    if (!decision)
                    {
                        RaceState.MissedCalls++;
                        Main.Log(string.Format("WEATHER: {0}, {1} — tyre change needed, but the call is late; staying out another lap.", RaceState.DriverName(v), RaceState.LapText()));
                    }
                    else
                    {
                        Main.Log(string.Format("WEATHER: {0}, {1} — director calls the tyre change.", RaceState.DriverName(v), RaceState.LapText()));
                    }
                }
                else
                {
                    decision = RaceState.WeatherDecision[v];
                }
                if (!decision)
                {
                    __result = false;
                }
            }
            catch (Exception e)
            {
                Main.Error("Weather call failed: " + e);
            }
        }
    }

    /// <summary>Undercut Artist: box a lap early to jump a close rival, when a stop is due soon anyway.</summary>
    [HarmonyPatch(typeof(SessionStrategy), "DoesVehicleNeedToPit")]
    internal static class UndercutPatch
    {
        private static readonly FieldInfo sReason = AccessTools.Field(typeof(SessionStrategy), "mReasonForPreviousPit");

        private static void Postfix(SessionStrategy __instance, bool inIsPitlaneEntryGate, ref bool __result)
        {
            if (__result || !inIsPitlaneEntryGate)
            {
                return;
            }
            RacingVehicle v = Context.VehicleOf(__instance);
            if (!Context.Worth(v) || !RaceState.InRace() || RaceState.Director == null || RaceState.Director.specialty != Specialty.UndercutArtist || !RaceState.IsControlled(v))
            {
                return;
            }
            try
            {
                PlanInfo plan;
                if (!RaceState.Plans.TryGetValue(v, out plan) || plan == null || plan.StopsRemaining(v) <= 0)
                {
                    return;
                }
                float lapLength = Math.Max(1f, v.pathController.GetPathData(PathController.PathType.Track).length);
                float tyreLapsLeft = v.setup.currentSetup.tyreSet.GetOptimalTyreDistance() / lapLength;
                int lapsLeft = Game.instance.sessionManager.GetLapsRemaining();
                // A real undercut is one lap early, from close range: within 1.0 s of the car ahead,
                // and the tyres (by measured wear) due within about a lap and a half anyway.
                float cliffLaps = RaceState.LastLapWear.ContainsKey(v.carID) ? RaceState.LapsToCliff(v) : tyreLapsLeft;
                if (tyreLapsLeft < 3f && cliffLaps <= 1.5f && v.timer.gapToAhead > 0f && v.timer.gapToAhead < 1.0f && lapsLeft > 3)
                {
                    RacingVehicle mate;
                    if (RaceState.TeamMateBusy(v, out mate) && !RaceState.UnderSafetyCar())
                    {
                        if (RaceState.UndercutSkipLogged.Add(v.timer.stops * 1000 + 500 + v.carID))
                        {
                            Main.Log(string.Format("UNDERCUT HELD: {0}, {1} — {2:0.0}s behind, but {3} is in the box; no double stack.",
                                RaceState.DriverName(v), RaceState.LapText(), v.timer.gapToAhead, RaceState.DriverName(mate)));
                        }
                        return;
                    }
                    // The stints still in the plan, on their best sets, have to cover the rest of the
                    // race; otherwise the early stop just buys a second, unplanned one.
                    int next = plan.NextStintIndex(v, false);
                    float reach = 0f;
                    string sets = "";
                    for (int si = Math.Max(0, next); si < plan.options.Length; si++)
                    {
                        TyreSet fresh = __instance.GetTyreInBestCondition(plan.options[si]);
                        if (fresh == null) continue;
                        reach += fresh.GetOptimalTyreDistance() / lapLength;
                        sets += (sets.Length > 0 ? "+" : "") + fresh.GetCompound();
                    }
                    // The game's tyre model can be optimistic; if this set is wearing faster than
                    // the model says, assume the next sets will too.
                    float seenWear;
                    float modelWear = lapLength / Math.Max(1f, v.setup.currentSetup.tyreSet.GetMaxDistance());
                    if (RaceState.LastLapWear.TryGetValue(v.carID, out seenWear) && seenWear > modelWear)
                    {
                        reach *= modelWear / seenWear;
                    }
                    if (reach < lapsLeft)
                    {
                        if (RaceState.UndercutSkipLogged.Add(v.timer.stops * 1000 + v.carID))
                        {
                            Main.Log(string.Format("UNDERCUT SKIPPED: {0}, {1} — {2:0.0}s behind, but the rest of the plan ({3}, ~{4:0} laps) wouldn't make the flag ({5} to go).",
                                RaceState.DriverName(v), RaceState.LapText(), v.timer.gapToAhead, sets.Length > 0 ? sets : "no sets", reach, lapsLeft));
                        }
                        return;
                    }
                    __result = true;
                    // Record it as a tyre stop (the game's reason field would otherwise keep the last stop's reason).
                    sReason.SetValue(__instance, SessionStints.ReasonForStint.PitTyreChange);
                    RaceState.UndercutPending.Add(v);
                    Main.Log(string.Format("UNDERCUT: {0}, {1} — {2:0.0}s behind; boxing a lap early to jump them.", RaceState.DriverName(v), RaceState.LapText(), v.timer.gapToAhead));
                }
            }
            catch (Exception e)
            {
                Main.Error("Undercut check failed: " + e);
            }
        }
    }

    /// <summary>
    /// Bring it home: in the last few laps, don't stop just because the tyres are past
    /// their best if they'll still reach the flag. Damage, wrong tread, refuelling and
    /// part repairs are left to the game.
    /// </summary>
    [HarmonyPatch(typeof(SessionStrategy), "DoesVehicleNeedToPit")]
    internal static class BringItHomePatch
    {
        public const int LastLaps = 3;

        private static void Postfix(SessionStrategy __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }
            RacingVehicle v = Context.VehicleOf(__instance);
            if (!Context.Worth(v) || !RaceState.InRace() || !RaceState.IsControlled(v))
            {
                return;
            }
            try
            {
                int lapsLeft = Game.instance.sessionManager.GetLapsRemaining();
                if (lapsLeft > LastLaps || __instance.reasonForPreviousPit != SessionStints.ReasonForStint.PitTyreChange)
                {
                    return;
                }
                TyreSet tyres = v.setup.currentSetup.tyreSet;
                if (tyres == null || tyres.hasLostWheel || tyres.hasWrongCompoundFitted)
                {
                    return;
                }
                float lapLength = Math.Max(1f, v.pathController.GetPathData(PathController.PathType.Track).length);
                // Wear per lap: what this set actually lost last lap if we've seen one, never
                // less than 80% of the game's model, plus a 10% margin.
                float modelWear = lapLength / Math.Max(1f, tyres.GetMaxDistance());
                float seen;
                float wear = RaceState.LastLapWear.TryGetValue(v.carID, out seen) && seen > 0f
                    ? Math.Max(seen, 0.8f * modelWear)
                    : modelWear;
                wear *= 1.1f;
                // Laps until the rubber hits the compound's cliff (where the big time loss starts).
                float lapsToCliff = (tyres.GetCondition() - tyres.GetCliffCondition()) / wear;
                if (lapsToCliff >= lapsLeft + 0.25f)
                {
                    __result = false;
                    if (RaceState.BringHomeLogged.Add(v.carID))
                    {
                        Main.Log(string.Format("BRING IT HOME: {0}, {1} — tyres {2:0%} ({3}, cliff {4:0%}), wearing ~{5:0.0%}/lap, ~{6:0.0} laps to the cliff for {7} to go; staying out.",
                            RaceState.DriverName(v), RaceState.LapText(), tyres.GetCondition(), tyres.GetCompound(), tyres.GetCliffCondition(), wear, lapsToCliff, lapsLeft));
                    }
                }
                else if (RaceState.BringHomeLogged.Add(v.carID))
                {
                    Main.Log(string.Format("BRING IT HOME? {0}, {1} — tyres {2:0%} ({3}, cliff {4:0%}), wearing ~{5:0.0%}/lap, only ~{6:0.0} laps to the cliff for {7} to go; pitting.",
                        RaceState.DriverName(v), RaceState.LapText(), tyres.GetCondition(), tyres.GetCompound(), tyres.GetCliffCondition(), wear, lapsToCliff, lapsLeft));
                }
            }
            catch (Exception e)
            {
                Main.Error("Bring-it-home check failed: " + e);
            }
        }
    }

    /// <summary>
    /// Track-position stretch (0.4.0): when a director car is running in the top 3, or 3+ places
    /// above its forecast, a routine tyre stop is put off (an overcut) for as long as the tyres stay
    /// clear of the cliff, so the car keeps its place instead of dropping into traffic. Stretching
    /// only shortens what the rest of the plan has to cover. Never under a safety car (a cheap
    /// stop), never against an undercut call, never for damage, weather or fuel, and the last few
    /// laps stay with bring-it-home.
    /// </summary>
    [HarmonyPatch(typeof(SessionStrategy), "DoesVehicleNeedToPit")]
    internal static class StretchPatch
    {
        public const float MinCliffLaps = 1.5f;

        private static void Postfix(SessionStrategy __instance, bool inIsPitlaneEntryGate, ref bool __result)
        {
            if (!__result || !inIsPitlaneEntryGate || Main.Settings == null || !Main.Settings.StretchForTrackPosition)
            {
                return;
            }
            RacingVehicle v = Context.VehicleOf(__instance);
            if (!Context.Worth(v) || !RaceState.InRace() || RaceState.Director == null || !RaceState.IsControlled(v))
            {
                return;
            }
            try
            {
                if (__instance.reasonForPreviousPit != SessionStints.ReasonForStint.PitTyreChange || RaceState.UnderSafetyCar() || RaceState.UndercutPending.Contains(v))
                {
                    return;
                }
                int lapsLeft = Game.instance.sessionManager.GetLapsRemaining();
                if (lapsLeft <= BringItHomePatch.LastLaps)
                {
                    return;
                }
                TyreSet tyres = v.setup.currentSetup.tyreSet;
                if (tyres == null || tyres.hasLostWheel || tyres.hasWrongCompoundFitted || tyres.GetTread() != SessionStrategy.GetRecommendedTreadRightNow())
                {
                    return;
                }
                int pos = v.standingsPosition;
                int expected = v.driver != null ? v.driver.expectedRacePosition : 99;
                bool top3 = pos > 0 && pos <= 3;
                bool ahead = pos > 0 && expected - pos >= 3;
                if (!top3 && !ahead)
                {
                    return;
                }
                float cliff = RaceState.LapsToCliff(v);
                int lap = Game.instance.sessionManager.lap;
                int logged;
                bool newLap = !RaceState.StretchLogged.TryGetValue(v.carID, out logged) || logged != lap;
                if (cliff < MinCliffLaps)
                {
                    if (newLap)
                    {
                        RaceState.StretchLogged[v.carID] = lap;
                        Main.Log(string.Format("STRETCH OVER: {0}, {1} — P{2}; tyres {3:0%} ({4}), only ~{5:0.0} laps to the cliff; boxing.",
                            RaceState.DriverName(v), RaceState.LapText(), pos, tyres.GetCondition(), tyres.GetCompound(), cliff));
                    }
                    return;
                }
                __result = false;
                if (newLap)
                {
                    RaceState.StretchLogged[v.carID] = lap;
                    Main.Log(string.Format("STRETCH: {0}, {1} — running P{2} (forecast P{3}); tyres {4:0%} ({5}, cliff {6:0%}), ~{7:0.0} laps to the cliff; staying out to hold track position.",
                        RaceState.DriverName(v), RaceState.LapText(), pos, expected, tyres.GetCondition(), tyres.GetCompound(), tyres.GetCliffCondition(), cliff));
                }
            }
            catch (Exception e)
            {
                Main.Error("Stretch check failed: " + e);
            }
        }
    }

    /// <summary>
    /// No double stacks. If the team-mate is already boxing, hold a routine tyre stop one lap
    /// so the second car doesn't queue behind the first. Double stacks are still allowed under
    /// a safety car or VSC, for weather tread changes, repairs, fuel, a lost wheel or wrong
    /// compound, and when the tyres can't do another lap. Spotting it depends on the director.
    /// </summary>
    [HarmonyPatch(typeof(SessionStrategy), "DoesVehicleNeedToPit")]
    [HarmonyPriority(Priority.Last)]
    internal static class StaggerPatch
    {
        private static void Postfix(SessionStrategy __instance, bool inIsPitlaneEntryGate, ref bool __result)
        {
            if (!__result || !inIsPitlaneEntryGate)
            {
                return;
            }
            RacingVehicle v = Context.VehicleOf(__instance);
            if (!Context.Worth(v) || !RaceState.InRace() || RaceState.Director == null || !RaceState.IsControlled(v))
            {
                return;
            }
            try
            {
                RacingVehicle mate;
                if (!RaceState.TeamMateBusy(v, out mate))
                {
                    return;
                }
                string mateName = RaceState.DriverName(mate);
                string why = null;
                TyreSet tyres = v.setup.currentSetup.tyreSet;
                if (RaceState.UnderSafetyCar()) why = "under the safety car, a double stack is the right call";
                else if (__instance.reasonForPreviousPit != SessionStints.ReasonForStint.PitTyreChange) why = "not a routine tyre stop (" + __instance.reasonForPreviousPit + ")";
                else if (tyres == null || tyres.hasLostWheel || tyres.hasWrongCompoundFitted) why = "tyre problem";
                else if (RaceState.LapsToCliff(v) < 1.25f) why = string.Format("tyres can't do another lap ({0:0%})", tyres.GetCondition());
                else if (Game.instance.sessionManager.GetLapsRemaining() <= 1) why = "last lap";

                int lap = Game.instance.sessionManager.lap;
                int key = v.carID;
                if (why != null)
                {
                    if (RaceState.StaggerLap.ContainsKey(key) && RaceState.StaggerLap[key] == lap) return;
                    RaceState.StaggerLap[key] = lap;
                    RaceState.StaggerHeld[key] = false;
                    Main.Log(string.Format("DOUBLE STACK: {0}, {1} — boxing behind {2}: {3}.", RaceState.DriverName(v), RaceState.LapText(), mateName, why));
                    return;
                }
                // One hold per stop: if we held on the previous lap, let it through now.
                int lastLap;
                bool held;
                if (RaceState.StaggerLap.TryGetValue(key, out lastLap) && RaceState.StaggerHeld.TryGetValue(key, out held) && held && lastLap == lap - 1)
                {
                    return;
                }
                bool decided;
                if (RaceState.StaggerLap.TryGetValue(key, out lastLap) && lastLap == lap && RaceState.StaggerHeld.TryGetValue(key, out decided))
                {
                    if (decided) __result = false;
                    return;
                }
                bool spotted = RaceState.Roll() <= RaceState.ReactionChance(v, false);
                RaceState.StaggerLap[key] = lap;
                RaceState.StaggerHeld[key] = spotted;
                if (spotted)
                {
                    __result = false;
                    RaceState.UndercutPending.Remove(v);
                    Main.Log(string.Format("STAGGER: {0}, {1} — {2} is in the box; staying out a lap (tyres {3:0%}, ~{4:0.0} laps to the cliff).",
                        RaceState.DriverName(v), RaceState.LapText(), mateName, tyres.GetCondition(), RaceState.LapsToCliff(v)));
                }
                else
                {
                    Main.Log(string.Format("STAGGER MISSED: {0}, {1} — the director didn't catch {2} already boxing; double stack.",
                        RaceState.DriverName(v), RaceState.LapText(), mateName));
                }
            }
            catch (Exception e)
            {
                Main.Error("Stagger check failed: " + e);
            }
        }
    }

    /// <summary>
    /// Pre-stagger: when both cars will need tyres within the next couple of laps (same plan,
    /// same wear), bring the one with less rubber in a lap early so the other doesn't have to
    /// queue behind it - or double stack because its tyres can't wait.
    /// </summary>
    [HarmonyPatch(typeof(SessionStrategy), "DoesVehicleNeedToPit")]
    [HarmonyPriority(Priority.Last)]
    internal static class PreStaggerPatch
    {
        private static readonly FieldInfo sReason = AccessTools.Field(typeof(SessionStrategy), "mReasonForPreviousPit");

        private static void Postfix(SessionStrategy __instance, bool inIsPitlaneEntryGate, ref bool __result)
        {
            if (__result || !inIsPitlaneEntryGate)
            {
                return;
            }
            RacingVehicle v = Context.VehicleOf(__instance);
            if (!Context.Worth(v) || !RaceState.InRace() || RaceState.Director == null || !RaceState.IsControlled(v))
            {
                return;
            }
            try
            {
                int lapsLeft = Game.instance.sessionManager.GetLapsRemaining();
                if (lapsLeft <= 3 || RaceState.UnderSafetyCar())
                {
                    return;
                }
                PlanInfo plan;
                if (!RaceState.Plans.TryGetValue(v, out plan) || plan == null || plan.StopsRemaining(v) <= 0)
                {
                    return;
                }
                RacingVehicle mate = Game.instance.vehicleManager.GetVehicleTeamMate(v);
                if (mate == null || mate.behaviourManager.isOutOfRace || !RaceState.IsControlled(mate) || RaceState.TeamMateBusy(v, out mate))
                {
                    return;
                }
                if (!RaceState.LastLapWear.ContainsKey(v.carID) || !RaceState.LastLapWear.ContainsKey(mate.carID))
                {
                    return;
                }
                PlanInfo matePlan;
                if (!RaceState.Plans.TryGetValue(mate, out matePlan) || matePlan == null || matePlan.StopsRemaining(mate) <= 0)
                {
                    return;
                }
                float mine = RaceState.LapsToCliff(v);
                float theirs = RaceState.LapsToCliff(mate);
                // Both due inside ~2 laps, and this car is the one that should go first.
                bool bothDue = mine < 2.25f && theirs < 2.25f;
                bool iGoFirst = mine < theirs || (Mathf.Approximately(mine, theirs) && v.carID < mate.carID);
                if (!bothDue || !iGoFirst)
                {
                    return;
                }
                if (RaceState.Roll() > RaceState.ReactionChance(v, false))
                {
                    return; // not spotted this lap
                }
                __result = true;
                sReason.SetValue(__instance, SessionStints.ReasonForStint.PitTyreChange);
                Main.Log(string.Format("STAGGER: {0}, {1} — both cars are due (~{2:0.0} and ~{3:0.0} laps to the cliff); {0} comes in a lap early so {4} won't queue.",
                    RaceState.DriverName(v), RaceState.LapText(), mine, theirs, RaceState.DriverName(mate)));
            }
            catch (Exception e)
            {
                Main.Error("Pre-stagger failed: " + e.Message);
            }
        }
    }

    /// <summary>Measure time a player car spends parked behind its team-mate in the pit box.</summary>
    [HarmonyPatch(typeof(PitboxEntryPathState), "SimulationUpdate")]
    internal static class BoxWaitPatch
    {
        private static void Postfix(PitboxEntryPathState __instance)
        {
            try
            {
                RacingVehicle v = __instance.racingVehicle;
                if (v == null || !v.isPlayerDriver || !v.setup.sessionPitStop.queuedCarBehindTeamMate || v.movementEnabled)
                {
                    return;
                }
                float w;
                RaceState.BoxWait.TryGetValue(v.carID, out w);
                RaceState.BoxWait[v.carID] = w + GameTimer.simulationDeltaTime;
            }
            catch (Exception)
            {
            }
        }
    }

    [HarmonyPatch(typeof(PitboxEntryPathState), "OnExit")]
    internal static class BoxWaitLogPatch
    {
        private static void Postfix(PitboxEntryPathState __instance)
        {
            try
            {
                RacingVehicle v = __instance.racingVehicle;
                float w;
                if (v == null || !v.isPlayerDriver || !RaceState.BoxWait.TryGetValue(v.carID, out w) || w <= 0f)
                {
                    return;
                }
                RaceState.BoxWait.Remove(v.carID);
                Main.Log(string.Format("BOX QUEUE: {0}, {1} — waited {2:0.0}s behind the team-mate in the pit box.", RaceState.DriverName(v), RaceState.LapText(), w));
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Remember each player car's tyre condition at every lap line, to measure real wear.</summary>
    [HarmonyPatch(typeof(PersistentEventData), "PostStintLapInfo")]
    internal static class LapWearPatch
    {
        // 0.2.2-0.4.1 keyed this by carID for every car in the field. carID is only the team slot
        // (0 or 1), so all 20 cars shared two keys and rivals' tyres were mixed into ours (the
        // "34.6%/lap" at Tondela). Now: player cars only, green-flag laps only, median of the stint.
        private static void Postfix(RacingVehicle inVehicle, float inTyreCondition)
        {
            try
            {
                if (inVehicle == null || !inVehicle.isPlayerDriver || !RaceState.InRace())
                {
                    return;
                }
                int key = inVehicle.carID;
                List<float> readings;
                if (!RaceState.WearReadings.TryGetValue(key, out readings))
                {
                    readings = new List<float>();
                    RaceState.WearReadings[key] = readings;
                }
                float last;
                bool have = RaceState.LastLapCondition.TryGetValue(key, out last);
                RaceState.LastLapCondition[key] = inTyreCondition;
                if (!have || inTyreCondition > last)
                {
                    readings.Clear(); // new set (or first lap): no reading yet
                    RaceState.LastLapWear.Remove(key);
                    return;
                }
                if (RaceState.UnderSafetyCar())
                {
                    RaceState.WearSkipNext.Add(key); // the lap it ends on is part-slow too
                    return; // slow laps would make the tyres look everlasting
                }
                if (RaceState.WearSkipNext.Remove(key))
                {
                    return;
                }
                float reading = last - inTyreCondition;
                float before = RaceState.MedianWear(readings);
                readings.Add(reading);
                if (readings.Count > 6) readings.RemoveAt(0);
                float smoothed = RaceState.MedianWear(readings);
                RaceState.LastLapWear[key] = smoothed;
                if (readings.Count >= 4 && before > 0f && reading > 2f * before)
                {
                    Main.Log(string.Format("WEAR: {0}, {1} — one-lap drop of {2:0.0%} (lock-up or contact?); planning on {3:0.0%}/lap.",
                        RaceState.DriverName(inVehicle), RaceState.LapText(), reading, smoothed));
                }
            }
            catch (Exception e)
            {
                Main.Error("Lap wear tracking failed: " + e.Message);
            }
        }
    }

    /// <summary>Log every stop the director makes, with the game's reason and the car's state.</summary>
    [HarmonyPatch(typeof(SessionStrategy), "Pit")]
    internal static class PitLogPatch
    {
        private static void Prefix(SessionStrategy __instance)
        {
            RacingVehicle v = Context.VehicleOf(__instance);
            if (!Context.Worth(v) || !RaceState.InRace() || !RaceState.IsControlled(v))
            {
                return;
            }
            try
            {
                if (__instance.IsGoingToPit())
                {
                    return;
                }
                TyreSet tyres = v.setup.currentSetup.tyreSet;
                string worst = "";
                float worstCondition = 2f;
                CarPart[] parts = v.car.seriesCurrentParts;
                for (int i = 0; parts != null && i < parts.Length; i++)
                {
                    float c = parts[i].partCondition.condition;
                    if (c < worstCondition)
                    {
                        worstCondition = c;
                        worst = parts[i].GetPartType().ToString();
                    }
                }
                string reason = RaceState.UndercutPending.Remove(v) ? "undercut" : Reason(__instance.reasonForPreviousPit);
                Main.Log(string.Format("PIT: {0}, {1} — {2}; tyres {3:0%} ({4}), worst part {5} {6:0%}.",
                    RaceState.DriverName(v), RaceState.LapText(), reason,
                    tyres != null ? tyres.GetCondition() : 0f, tyres != null ? tyres.GetCompound().ToString() : "?", worst, worstCondition));
            }
            catch (Exception e)
            {
                Main.Error("Pit log failed: " + e);
            }
        }

        private static string Reason(SessionStints.ReasonForStint r)
        {
            switch (r)
            {
                case SessionStints.ReasonForStint.PitTyreChange: return "tyres worn";
                case SessionStints.ReasonForStint.PitTyreThreadChange: return "weather tyre change";
                case SessionStints.ReasonForStint.PitConditionFix: return "part repair";
                case SessionStints.ReasonForStint.PitRefuel: return "fuel";
                case SessionStints.ReasonForStint.DriverSwap: return "driver swap";
                case SessionStints.ReasonForStint.None: return "unplanned stop (game or manual)";
                default: return r.ToString();
            }
        }
    }

    // ----------------------------------------------------------------------
    // End of race: relationships and the summary.
    // ----------------------------------------------------------------------

    [HarmonyPatch(typeof(RaceDirector), "OnSessionEnd")]
    internal static class RaceEndPatch
    {
        private static void Prefix()
        {
            SessionDetails.SessionType type;
            try
            {
                type = Game.instance.sessionManager.sessionType;
            }
            catch (Exception)
            {
                return;
            }
            if (type == SessionDetails.SessionType.Qualifying)
            {
                // Practice/qualifying end: keep the weekend state; review the plan after the final qualifying session.
                Weekend.ReviewAfterQualifying();
                return;
            }
            if (type != SessionDetails.SessionType.Race)
            {
                return;
            }
            try
            {
                if (!Main.Enabled || RaceState.Director == null)
                {
                    return;
                }
                StrategyDirector d = RaceState.Director;
                StringBuilder sb = new StringBuilder();
                sb.Append(string.Format("RACE SUMMARY: {0}; {1} call(s), {2} missed.", RaceState.CarPlansText(), RaceState.Calls, RaceState.MissedCalls));
                RacingVehicle[] cars = Game.instance.vehicleManager.GetPlayerVehicles();
                for (int i = 0; cars != null && i < cars.Length; i++)
                {
                    RacingVehicle v = cars[i];
                    if (v == null || v.driver == null)
                    {
                        continue;
                    }
                    DirectorRelationship rel = d.GetRelationship(v.driver);
                    float before = rel.amount;
                    float gain = 8f;
                    if (v.standingsPosition <= v.driver.expectedRacePosition)
                    {
                        gain += 4f;
                    }
                    rel.racesTogether++;
                    rel.amount = Mathf.Min(100f, rel.amount + gain);
                    string unlocked = "";
                    if (before < 50f && rel.HasTrust) unlocked = " — unlocked TRUST";
                    if (before < 99.5f && rel.InSync) unlocked = " — unlocked IN SYNC";
                    sb.Append(string.Format(" {0}: P{1} (expected P{2}), relationship {3:0}%{4}.", v.driver.name, v.standingsPosition, v.driver.expectedRacePosition, rel.amount, unlocked));
                }
                d.racesRun++;
                Main.Log(sb.ToString());
            }
            catch (Exception e)
            {
                Main.Error("Race summary failed: " + e);
            }
            finally
            {
                try
                {
                    if (Game.instance.sessionManager.sessionType == SessionDetails.SessionType.Race)
                    {
                        RaceState.EndedKey = Game.instance.sessionManager.eventDetails.eventDate.Ticks;
                    }
                }
                catch (Exception)
                {
                }
                RaceState.Reset();
            }
        }
    }
}
