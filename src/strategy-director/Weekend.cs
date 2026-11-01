using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace StrategyDirectorMod
{
    /// <summary>
    /// Practice and qualifying run to the race plan, and the post-qualifying review.
    /// </summary>
    internal static class Weekend
    {
        // ------------------------------------------------------------------
        // What the plan implies for the weekend
        // ------------------------------------------------------------------

        public static SessionStrategy.TyreOption Hardest()
        {
            try
            {
                return Game.instance.sessionManager.championship.rules.compoundsAvailable >= 3 ? SessionStrategy.TyreOption.Third : SessionStrategy.TyreOption.Second;
            }
            catch (Exception)
            {
                return SessionStrategy.TyreOption.Second;
            }
        }

        /// <summary>The tyre this plan likes to start the race on.</summary>
        public static SessionStrategy.TyreOption StartCompound(PlanType plan)
        {
            switch (plan)
            {
                case PlanType.FewestStops: return SessionStrategy.TyreOption.Second;
                case PlanType.Gamble: return Hardest();
                default: return SessionStrategy.TyreOption.First;
            }
        }

        /// <summary>Compounds to learn in practice, most important first.</summary>
        public static List<SessionStrategy.TyreOption> PracticeCompounds(PlanType plan)
        {
            List<SessionStrategy.TyreOption> list = new List<SessionStrategy.TyreOption>();
            switch (plan)
            {
                case PlanType.FewestStops:
                    list.Add(SessionStrategy.TyreOption.Second);
                    list.Add(Hardest());
                    list.Add(SessionStrategy.TyreOption.First);
                    break;
                case PlanType.Gamble:
                    list.Add(Hardest());
                    list.Add(SessionStrategy.TyreOption.First);
                    break;
                default:
                    list.Add(SessionStrategy.TyreOption.First);
                    list.Add(SessionStrategy.TyreOption.Second);
                    break;
            }
            List<SessionStrategy.TyreOption> distinct = new List<SessionStrategy.TyreOption>();
            foreach (SessionStrategy.TyreOption o in list)
            {
                if (!distinct.Contains(o)) distinct.Add(o);
            }
            return distinct;
        }

        private static PracticeReportSessionData.KnowledgeType KnowledgeFor(SessionStrategy.TyreOption o)
        {
            switch (o)
            {
                case SessionStrategy.TyreOption.Second: return PracticeReportSessionData.KnowledgeType.SecondOptionTyres;
                case SessionStrategy.TyreOption.Third: return PracticeReportSessionData.KnowledgeType.ThirdOptionTyres;
                default: return PracticeReportSessionData.KnowledgeType.FirstOptionTyres;
            }
        }

        public static float Knowledge(RacingVehicle v, PracticeReportSessionData.KnowledgeType type)
        {
            try
            {
                PracticeReportSessionData report = v.practiceKnowledge.practiceReport;
                return report == null ? 1f : report.GetKnowledgeOfType(type).normalizedKnowledge;
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        /// <summary>Practice priority: qualifying trim first (where qualifying sets the grid), then the race compounds.</summary>
        public static bool WantsQualifyingTrim(RacingVehicle v)
        {
            try
            {
                return v.championship.rules.qualifyingBasedActive && Knowledge(v, PracticeReportSessionData.KnowledgeType.QualifyingTrim) < 1f;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// In "top 10 start on their Q2 tyre" rules, qualify on the race start compound
        /// when the car is in the hunt for Q3; otherwise the softest tyre.
        /// </summary>
        public static SessionStrategy.TyreOption QualifyingCompound(RacingVehicle v)
        {
            PlanType plan = RaceState.PlanFor(v);
            SessionStrategy.TyreOption start = StartCompound(plan);
            try
            {
                bool lockRule = v.championship.rules.gridSetup == ChampionshipRules.GridSetup.QualifyingBased3Sessions;
                int threshold = GameStatsConstants.qualifyingThresholdForQ3;
                bool inHunt = v.driver.expectedRacePosition <= threshold + 3 || (v.standingsPosition > 0 && v.standingsPosition <= threshold + 3);
                if (lockRule && inHunt)
                {
                    return start;
                }
            }
            catch (Exception)
            {
            }
            return SessionStrategy.TyreOption.First;
        }

        // ------------------------------------------------------------------
        // Tyre choice in practice and qualifying (called from TyrePlanPatch)
        // ------------------------------------------------------------------

        public static void ChooseSessionTyre(SessionStrategy strategy, RacingVehicle v, ref TyreSet result)
        {
            if (result.GetTread() != TyreSet.Tread.Slick)
            {
                string wet = "wet track — the game picks the tyre";
                string lastWet;
                if (!RaceState.LastRunLogged.TryGetValue(v, out lastWet) || lastWet != wet)
                {
                    RaceState.LastRunLogged[v] = wet;
                    Main.Log(string.Format("{0} RUN: {1} — {2}.", RaceState.SessionName(Game.instance.sessionManager), RaceState.DriverName(v), wet));
                }
                return; // wet running is the game's call
            }
            SessionManager s = Game.instance.sessionManager;
            SessionStrategy.TyreOption? pick = null;
            string why = null;

            TyreSet chosen = null;
            if (s.sessionType == SessionDetails.SessionType.Practice)
            {
                // Candidate runs in priority order; each must find a set that doesn't eat a race set.
                List<KeyValuePair<SessionStrategy.TyreOption, string>> runs = new List<KeyValuePair<SessionStrategy.TyreOption, string>>();
                if (WantsQualifyingTrim(v))
                {
                    runs.Add(new KeyValuePair<SessionStrategy.TyreOption, string>(QualifyingCompound(v), "qualifying trim"));
                }
                foreach (SessionStrategy.TyreOption o in PracticeCompounds(RaceState.PlanFor(v)))
                {
                    if (Knowledge(v, KnowledgeFor(o)) < 1f)
                    {
                        runs.Add(new KeyValuePair<SessionStrategy.TyreOption, string>(o, "race trim, learning the race tyre"));
                    }
                }
                foreach (SessionStrategy.TyreOption o in PracticeCompounds(RaceState.PlanFor(v)))
                {
                    runs.Add(new KeyValuePair<SessionStrategy.TyreOption, string>(o, "race trim, long run"));
                }
                foreach (KeyValuePair<SessionStrategy.TyreOption, string> run in runs)
                {
                    TyreSet set = PracticeSet(strategy, run.Key);
                    if (set != null)
                    {
                        pick = run.Key;
                        why = run.Value + (set.GetCondition() < 0.999f ? string.Format(", used set {0:0%}", set.GetCondition()) : ", fresh set");
                        chosen = set;
                        break;
                    }
                }
                if (chosen == null)
                {
                    return; // nothing spare: let the game pick (it protects sets too)
                }
            }
            else if (s.sessionType == SessionDetails.SessionType.Qualifying)
            {
                bool locking = s.eventDetails.IsLockingTyresThisSession();
                pick = QualifyingCompound(v);
                string run = v.timer.HasSetLapTime() ? "final run" : "banker";
                why = locking ? run + ", fresh set; this tyre is locked in for the race start" : run + ", fresh set";
                // Fresh rubber for every qualifying run (Kyle, 10/26): the best-condition set of the compound.
            }

            if (pick == null)
            {
                return;
            }
            TyreSet tyre = chosen ?? strategy.GetTyreInBestCondition(pick.Value);
            if (tyre == null)
            {
                return;
            }
            result = tyre;
            string line = string.Format("{0} on {1} ({2})", why, tyre.GetCompound(), StrategyDirector.PlanName(RaceState.PlanFor(v)));
            string last;
            if (!RaceState.LastRunLogged.TryGetValue(v, out last) || last != line)
            {
                RaceState.LastRunLogged[v] = line;
                Main.Log(string.Format("{0} RUN: {1} — {2}.", RaceState.SessionName(s), RaceState.DriverName(v), line));
            }
        }

        /// <summary>
        /// A practice set that won't cost the race: the most-used set with some life left,
        /// or a fresh one only if enough new sets of that compound remain for the race.
        /// </summary>
        public const int MintSetsKeptForRace = 2;

        public static TyreSet PracticeSet(SessionStrategy strategy, SessionStrategy.TyreOption option)
        {
            TyreSet[] sets;
            try
            {
                sets = strategy.GetTyres(option);
            }
            catch (Exception)
            {
                return null;
            }
            if (sets == null || sets.Length == 0)
            {
                return null;
            }
            TyreSet used = null;
            foreach (TyreSet t in sets)
            {
                float c = t.GetCondition();
                if (c < 0.999f && c >= 0.35f && !t.hasLostWheel && !t.hasWrongCompoundFitted && (used == null || c < used.GetCondition()))
                {
                    used = t;
                }
            }
            if (used != null)
            {
                return used;
            }
            if (strategy.GetMintConditionTyreCount(option) > MintSetsKeptForRace)
            {
                return strategy.GetTyreInBestCondition(option);
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Post-qualifying review
        // ------------------------------------------------------------------

        public static void ReviewAfterQualifying()
        {
            try
            {
                if (!Main.Enabled || !Main.Settings.ReviewAfterQualifying)
                {
                    return;
                }
                RaceState.Ensure();
                StrategyDirector d = RaceState.Director;
                SessionManager s = Game.instance.sessionManager;
                if (d == null || !s.eventDetails.IsLastSessionOfType())
                {
                    return;
                }
                RacingVehicle[] cars = Game.instance.vehicleManager.GetPlayerVehicles();
                StringBuilder popup = new StringBuilder();
                for (int i = 0; cars != null && i < cars.Length; i++)
                {
                    RacingVehicle v = cars[i];
                    if (v == null || v.driver == null)
                    {
                        continue;
                    }
                    PlanType current = RaceState.PlanFor(v);
                    if (current == PlanType.Manual)
                    {
                        continue;
                    }
                    int grid = v.standingsPosition;
                    int expected = v.driver.expectedRacePosition;
                    int delta = grid - expected; // positive = qualified worse than expected
                    PlanType next = current;
                    string reason = null;
                    if (delta >= 4)
                    {
                        next = PlanType.Gamble;
                        reason = string.Format("starts P{0}, {1} places behind where we expect to finish — going for an offset strategy and hoping for a safety car", grid, delta);
                    }
                    else if (delta >= 2 && current != PlanType.Gamble)
                    {
                        next = PlanType.Attack;
                        reason = string.Format("starts P{0}, {1} places down — switching to attack to make up ground", grid, delta);
                    }
                    else if (delta <= -3)
                    {
                        next = PlanType.FewestStops;
                        reason = string.Format("starts P{0}, {1} places ahead of expectations — protecting track position with fewer stops", grid, -delta);
                    }

                    if (next == current || reason == null)
                    {
                        Main.Log(string.Format("QUALI REVIEW: {0} qualified P{1} (expected P{2}) — sticking with {3}.", v.driver.name, grid, expected, StrategyDirector.PlanName(current)));
                        continue;
                    }
                    // A director has to spot it: chance scales with Planning.
                    float spot = 0.6f + 0.4f * Mathf.Clamp01(d.stats.planning / 20f);
                    if (RaceState.Roll() > spot)
                    {
                        Main.Log(string.Format("QUALI REVIEW: {0} qualified P{1} (expected P{2}) — the director didn't adjust; still {3}.", v.driver.name, grid, expected, StrategyDirector.PlanName(current)));
                        continue;
                    }
                    d.SetDriverPlan(v.driver, i, next, RaceState.EventDate);
                    Main.Log(string.Format("QUALI REVIEW: {0} {1}. Plan {2} → {3}.", v.driver.name, reason, StrategyDirector.PlanName(current), StrategyDirector.PlanName(next)));
                    popup.AppendLine(string.Format("{0}: {1}. New plan: {2}.", v.driver.lastName, reason, StrategyDirector.PlanName(next)));
                }
                if (popup.Length > 0)
                {
                    popup.AppendLine();
                    popup.Append("Change it in Ctrl+F10 → Strategy Director if you disagree.");
                    FeedbackPopup.Open(d.shortName + " — race plan update", popup.ToString());
                }
            }
            catch (Exception e)
            {
                Main.Error("Qualifying review failed: " + e);
            }
        }
    }

    /// <summary>Practice trim: qualifying trim until learned (where qualifying sets the grid), then race trim. No coin flip.</summary>
    [HarmonyPatch(typeof(SessionSetup), "SetTargetTrim", new Type[0])]
    internal static class PracticeTrimPatch
    {
        private static readonly FieldInfo sVehicle = AccessTools.Field(typeof(SessionSetup), "mVehicle");

        private static void Postfix(SessionSetup __instance)
        {
            try
            {
                RacingVehicle v = sVehicle.GetValue(__instance) as RacingVehicle;
                if (!Context.Worth(v) || Game.instance.sessionManager.sessionType != SessionDetails.SessionType.Practice || !RaceState.IsControlled(v))
                {
                    return;
                }
                bool quali = Weekend.WantsQualifyingTrim(v);
                SessionSetup.Trim trim = quali ? SessionSetup.Trim.Qualifying : SessionSetup.Trim.Race;
                __instance.currentSetup.trim = trim;
                __instance.SetTargetTrim(trim);
                v.practiceKnowledge.knowledgeType = quali ? PracticeReportSessionData.KnowledgeType.QualifyingTrim : PracticeReportSessionData.KnowledgeType.RaceTrim;
            }
            catch (Exception e)
            {
                Main.Error("Practice trim failed: " + e);
            }
        }
    }
}

namespace StrategyDirectorMod
{
    /// <summary>The AI rule table can flip trim/knowledge mid-session; for director-run cars, the run plan stands.</summary>
    [HarmonyLib.HarmonyPatch(typeof(SessionAIOrder), "ApplyKnowledgeType")]
    internal static class PracticeOrderGuardPatch
    {
        private static bool Prefix(RacingVehicle inVehicle)
        {
            try
            {
                if (Context.Worth(inVehicle) && Game.instance.sessionManager.sessionType == SessionDetails.SessionType.Practice && RaceState.IsControlled(inVehicle))
                {
                    return false;
                }
            }
            catch (System.Exception)
            {
            }
            return true;
        }
    }
}
