using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace StrategyDirectorMod
{
    /// <summary>
    /// Lap-by-lap tyre-tread outlook from a session's water-level curve, using the
    /// same thresholds as the game's own SessionStrategy.GetRecommendedTreadForTime.
    /// </summary>
    internal static class Forecast
    {
        public class Phase
        {
            public int fromLap;   // 1-based
            public TyreSet.Tread tread;
        }

        public static TyreSet.Tread TreadAt(SessionWeatherDetails weather, float normalizedTime)
        {
            float water = weather.sessionWaterLevelCurve.curve.Evaluate(Mathf.Clamp01(normalizedTime));
            TyreDesignData data = DesignDataManager.instance.tyreData;
            if (water > data.maxLightTreadSurfaceWaterRange)
            {
                return TyreSet.Tread.HeavyTread;
            }
            if (water > data.maxSlickTreadSurfaceWaterRange)
            {
                return TyreSet.Tread.LightTread;
            }
            return TyreSet.Tread.Slick;
        }

        public static TyreSet.Tread TreadForLap(SessionWeatherDetails weather, int lap, int lapCount)
        {
            return TreadAt(weather, lapCount <= 0 ? 0f : (float)lap / lapCount);
        }

        public static List<Phase> Phases(SessionWeatherDetails weather, int lapCount)
        {
            List<Phase> phases = new List<Phase>();
            if (weather == null || weather.sessionWaterLevelCurve == null || weather.sessionWaterLevelCurve.curve == null || lapCount <= 0)
            {
                return phases;
            }
            for (int lap = 1; lap <= lapCount; lap++)
            {
                TyreSet.Tread t = TreadForLap(weather, lap - 1, lapCount);
                if (phases.Count == 0 || phases[phases.Count - 1].tread != t)
                {
                    phases.Add(new Phase { fromLap = lap, tread = t });
                }
            }
            return phases;
        }

        /// <summary>First lap after <paramref name="fromLap"/> that needs a non-slick tyre, or -1.</summary>
        public static int NextWetLap(SessionWeatherDetails weather, int fromLap, int lapCount)
        {
            if (weather == null || weather.sessionWaterLevelCurve == null || weather.sessionWaterLevelCurve.curve == null)
            {
                return -1;
            }
            for (int lap = Math.Max(1, fromLap + 1); lap <= lapCount; lap++)
            {
                if (TreadForLap(weather, lap - 1, lapCount) != TyreSet.Tread.Slick)
                {
                    return lap;
                }
            }
            return -1;
        }

        public static string TreadName(TyreSet.Tread t)
        {
            switch (t)
            {
                case TyreSet.Tread.LightTread: return "inters";
                case TyreSet.Tread.HeavyTread: return "full wets";
                default: return "slicks";
            }
        }

        /// <summary>"Slicks laps 1–7 · inters from ~lap 8 · ..." with a director's error applied to each crossover.</summary>
        public static string Describe(List<Phase> phases, int lapCount, int errorLaps)
        {
            if (phases.Count == 0)
            {
                return null;
            }
            if (phases.Count == 1)
            {
                return phases[0].tread == TyreSet.Tread.Slick ? "dry all race" : TreadName(phases[0].tread) + " all race";
            }
            StringBuilder sb = new StringBuilder();
            int previous = 1;
            for (int i = 0; i < phases.Count; i++)
            {
                if (i > 0) sb.Append(" · ");
                // Shift every crossover by the same error, but keep them in order.
                int lap = i == 0 ? 1 : Mathf.Clamp(Math.Max(previous + 1, phases[i].fromLap + errorLaps), 2, lapCount);
                previous = lap;
                sb.Append(i == 0
                    ? string.Format("{0} from the start", TreadName(phases[i].tread))
                    : string.Format("{0} from ~lap {1}", TreadName(phases[i].tread), lap));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Imperfect information. The base game's forecast is exact; the team's
        // Logistics Centre (weather perks 1-3) and the director's skill decide
        // how far off the director's read of it is. Never perfect.
        // ------------------------------------------------------------------

        public static int WeatherHQLevel(Team team)
        {
            try
            {
                if (team.perksManager.CheckPerkUnlocked(TeamPerk.Type.ForecastWeather3)) return 3;
                if (team.perksManager.CheckPerkUnlocked(TeamPerk.Type.ForecastWeather2)) return 2;
                if (team.perksManager.CheckPerkUnlocked(TeamPerk.Type.ForecastWeather1)) return 1;
            }
            catch (Exception)
            {
            }
            return 0;
        }

        /// <summary>
        /// Typical size of the timing error, in laps. Scales with race length: the same
        /// forecast uncertainty is a couple of laps in a sprint and many in a long race.
        /// (Logistics Centre none/1/2/3 ≈ 18/13/8/5% of the race distance.)
        /// </summary>
        public static float SpreadLaps(StrategyDirector d, Team team, int raceLaps)
        {
            float[] byLevel = { 0.18f, 0.13f, 0.08f, 0.05f };
            float spread = byLevel[Mathf.Clamp(WeatherHQLevel(team), 0, 3)] * Mathf.Max(1, raceLaps);
            if (d != null)
            {
                spread *= 1.25f - 0.5f * Mathf.Clamp01(d.stats.planning / 20f);   // 0.75x .. 1.25x
                if (d.specialty == Specialty.WeatherReader)
                {
                    spread *= 0.5f;
                }
            }
            return Mathf.Max(0.75f, spread);
        }

        /// <summary>Chance the director misses a wet spell entirely and plans for a dry race.</summary>
        public static float MissChance(StrategyDirector d, Team team)
        {
            float[] byLevel = { 0.25f, 0.15f, 0.08f, 0.04f };
            float chance = byLevel[Mathf.Clamp(WeatherHQLevel(team), 0, 3)];
            if (d != null && d.specialty == Specialty.WeatherReader)
            {
                chance *= 0.5f;
            }
            return chance;
        }

        public static int RollError(StrategyDirector d, Team team, int raceLaps)
        {
            return Mathf.RoundToInt(RaceState.Gaussian() * SpreadLaps(d, team, raceLaps));
        }

        public static string Accuracy(StrategyDirector d, Team team, int raceLaps)
        {
            int level = WeatherHQLevel(team);
            int band = Mathf.Max(1, Mathf.RoundToInt(SpreadLaps(d, team, raceLaps)));
            string hq = level == 0 ? "without a Logistics Centre" : "with the Logistics Centre at level " + level;
            return string.Format("{0} I'd say give or take {1} lap{2} on the timing", hq, band, band == 1 ? "" : "s");
        }
    }
}
