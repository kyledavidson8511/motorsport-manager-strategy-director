using System;
using System.Collections.Generic;
using FullSerializer;
using UnityEngine;

// NOTE: these types are written into save files by the game's own serializer.
// Never rename or move them, and only ever ADD fields (with defaults); renaming
// breaks loading of every save that has a director.
namespace StrategyDirectorMod
{
    public enum Specialty
    {
        None,
        TyreWhisperer,
        WeatherReader,
        Gambler,
        SafetyFirst,
        UndercutArtist
    }

    public enum PlanType
    {
        None = 0,
        Fastest = 1,
        FewestStops = 2,
        Attack = 3,
        Gamble = 4,
        Manual = 5
    }

    [fsObject(MemberSerialization = fsMemberSerialization.OptOut)]
    public class DirectorStats : PersonStats
    {
        /// <summary>0-20. How accurate the plan forecasts are.</summary>
        public float planning = 10f;

        /// <summary>0-20. How often the right call comes on the right lap.</summary>
        public float reactions = 10f;

        public float potential = 12f;

        public DirectorStats()
        {
        }

        public override float GetAbility()
        {
            return Mathf.Clamp((planning + reactions) / 40f * 5f, 0f, 5f);
        }

        public override float GetAbilityPotential()
        {
            return Mathf.Clamp(Mathf.Max(potential, (planning + reactions) / 2f) / 20f * 5f, 0f, 5f);
        }
    }

    [fsObject(MemberSerialization = fsMemberSerialization.OptOut)]
    public class DirectorRelationship
    {
        public string driverName = string.Empty;
        public float amount;
        public int racesTogether;

        public bool HasTrust => amount >= 50f;
        public bool InSync => amount >= 99.5f;
    }

    [fsObject(MemberSerialization = fsMemberSerialization.OptOut)]
    public class StrategyDirector : Person
    {
        public DirectorStats stats = new DirectorStats();
        public Specialty specialty = Specialty.None;
        public List<DirectorRelationship> directorRelationships = new List<DirectorRelationship>();

        // The plan Kyle picked for the upcoming race (or the director's pick if he didn't answer).
        public PlanType chosenPlan = PlanType.None;
        public DateTime chosenPlanEventDate = default(DateTime);
        public DateTime lastBriefingEventDate = default(DateTime);

        public int racesRun;

        // 0.5.0: the contract end date a renewal offer was last sent for (so it's sent once per contract).
        public DateTime renewalOfferSentFor = default(DateTime);

        // Per-car plan overrides for one event (e.g. after a poor qualifying). 0 = follow the team plan.
        public List<int> carPlans = new List<int>();
        public DateTime carPlansEventDate = default(DateTime);

        // 0.4.2: per-driver plan overrides (driver Guid strings, parallel lists), so a plan set for a
        // driver follows the driver whatever car slot the game puts them in for a session.
        public List<string> driverPlanIds = new List<string>();
        public List<int> driverPlanValues = new List<int>();
        public DateTime driverPlansEventDate = default(DateTime);

        public void SetDriverPlan(Driver driver, int carIndex, PlanType plan, DateTime eventDate)
        {
            SetCarPlan(carIndex, plan, eventDate);
            if (driver == null)
            {
                return;
            }
            if (driverPlansEventDate != eventDate)
            {
                driverPlanIds.Clear();
                driverPlanValues.Clear();
                driverPlansEventDate = eventDate;
            }
            string key = driver.id.ToString();
            int i = driverPlanIds.IndexOf(key);
            if (i < 0)
            {
                driverPlanIds.Add(key);
                driverPlanValues.Add((int)plan);
            }
            else
            {
                driverPlanValues[i] = (int)plan;
            }
        }

        /// <summary>The driver's own override if set for this event, else the car slot's, else the team plan.</summary>
        public PlanType PlanForDriver(Driver driver, int carIndex, DateTime eventDate)
        {
            if (driver != null && driverPlansEventDate == eventDate)
            {
                int i = driverPlanIds.IndexOf(driver.id.ToString());
                if (i >= 0 && i < driverPlanValues.Count && driverPlanValues[i] != 0)
                {
                    return (PlanType)driverPlanValues[i];
                }
                return TeamPlanFor(eventDate); // driver-keyed list exists for this event: slots no longer apply
            }
            return PlanForCar(carIndex, eventDate);
        }

        public PlanType TeamPlanFor(DateTime eventDate)
        {
            PlanType p = chosenPlanEventDate == eventDate ? chosenPlan : PlanType.None;
            return p == PlanType.None ? PlanType.Fastest : p;
        }

        public PlanType PlanForCar(int carIndex, DateTime eventDate)
        {
            if (carPlansEventDate == eventDate && carIndex >= 0 && carIndex < carPlans.Count && carPlans[carIndex] != 0)
            {
                return (PlanType)carPlans[carIndex];
            }
            return TeamPlanFor(eventDate);
        }

        public void SetCarPlan(int carIndex, PlanType plan, DateTime eventDate)
        {
            if (carPlansEventDate != eventDate)
            {
                carPlans.Clear();
                carPlansEventDate = eventDate;
            }
            while (carPlans.Count <= carIndex)
            {
                carPlans.Add(0);
            }
            carPlans[carIndex] = (int)plan;
        }

        public StrategyDirector()
        {
        }

        public override PersonStats GetStats()
        {
            return stats;
        }

        public override float GetStatsValue()
        {
            return stats.GetAbility();
        }

        public DirectorRelationship GetRelationship(Driver driver)
        {
            if (driver == null)
            {
                return null;
            }
            for (int i = 0; i < directorRelationships.Count; i++)
            {
                if (directorRelationships[i].driverName == driver.name)
                {
                    return directorRelationships[i];
                }
            }
            DirectorRelationship created = new DirectorRelationship { driverName = driver.name };
            directorRelationships.Add(created);
            return created;
        }

        public static string SpecialtyName(Specialty s)
        {
            switch (s)
            {
                case Specialty.TyreWhisperer: return "Tyre Whisperer";
                case Specialty.WeatherReader: return "Weather Reader";
                case Specialty.Gambler: return "Gambler";
                case Specialty.SafetyFirst: return "Safety First";
                case Specialty.UndercutArtist: return "Undercut Artist";
                default: return "Generalist";
            }
        }

        public static string SpecialtyBlurb(Specialty s)
        {
            switch (s)
            {
                case Specialty.TyreWhisperer: return "reads tyre life better than anyone; plans are more accurate";
                case Specialty.WeatherReader: return "never misses a wet/dry crossover";
                case Specialty.Gambler: return "always has a bold option and runs the cars harder";
                case Specialty.SafetyFirst: return "runs conservatively to protect the car";
                case Specialty.UndercutArtist: return "pits a lap early to jump a close rival";
                default: return "solid all-rounder";
            }
        }

        public static string PlanName(PlanType p)
        {
            switch (p)
            {
                case PlanType.Fastest: return "Fastest on paper";
                case PlanType.FewestStops: return "Fewest stops";
                case PlanType.Attack: return "Attack";
                case PlanType.Gamble: return "Offset / safety-car gamble";
                case PlanType.Manual: return "Manual (you run it)";
                default: return "None";
            }
        }

        public static string StarText(float ability)
        {
            return ability.ToString("0.0") + "★";
        }
    }
}
