using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StrategyDirectorMod
{
    /// <summary>Hiring, firing, wages, and the two mails (applicants and pre-race briefing).</summary>
    internal static class DirectorService
    {
        public const string RuleTag = "SDMOD";
        public const string ButtonPrefix = "SDMOD|";

        // ------------------------------------------------------------------
        // Lookup
        // ------------------------------------------------------------------

        public static Team PlayerTeam
        {
            get
            {
                try
                {
                    Player p = Game.instance.player;
                    return p == null || p.IsUnemployed() ? null : p.team;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public static StrategyDirector FindDirector(Team team)
        {
            if (team == null)
            {
                return null;
            }
            List<Person> staff = team.contractManager.GetAllPeopleOnJob(Contract.Job.Staff);
            for (int i = 0; i < staff.Count; i++)
            {
                StrategyDirector d = staff[i] as StrategyDirector;
                if (d != null && d.contract.contractStatus != Contract.ContractStatus.Terminated)
                {
                    return d;
                }
            }
            return null;
        }

        public static StrategyDirector Current => FindDirector(PlayerTeam);

        // ------------------------------------------------------------------
        // Candidates
        // ------------------------------------------------------------------

        internal class CandidateSpec
        {
            public string first;
            public string last;
            public string countryKey;
            public int gender;
            public long dobTicks;
            public float planning;
            public float reactions;
            public float potential;
            public Specialty specialty;
            public int yearlyWage;

            public float Stars => Mathf.Clamp((planning + reactions) / 40f * 5f, 0f, 5f);

            public string Encode()
            {
                return string.Join("|", new[]
                {
                    "HIRE", Clean(first), Clean(last), countryKey ?? "", gender.ToString(), dobTicks.ToString(),
                    planning.ToString("0.0"), reactions.ToString("0.0"), potential.ToString("0.0"),
                    ((int)specialty).ToString(), yearlyWage.ToString()
                });
            }

            public static CandidateSpec Decode(string[] f)
            {
                // f[0] == "HIRE"
                return new CandidateSpec
                {
                    first = f[1],
                    last = f[2],
                    countryKey = f[3],
                    gender = int.Parse(f[4]),
                    dobTicks = long.Parse(f[5]),
                    planning = float.Parse(f[6], System.Globalization.CultureInfo.InvariantCulture),
                    reactions = float.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture),
                    potential = float.Parse(f[8], System.Globalization.CultureInfo.InvariantCulture),
                    specialty = (Specialty)int.Parse(f[9]),
                    yearlyWage = int.Parse(f[10])
                };
            }

            private static string Clean(string s)
            {
                return (s ?? "").Replace("|", " ");
            }
        }

        private static readonly System.Random sRandom = new System.Random();

        private static float Range(float min, float max)
        {
            return min + (float)sRandom.NextDouble() * (max - min);
        }

        public static long AverageMechanicWage(Team team)
        {
            long total = 0;
            int n = 0;
            List<Person> mechanics = team.contractManager.GetAllPeopleOnJob(Contract.Job.Mechanic);
            for (int i = 0; i < mechanics.Count; i++)
            {
                total += mechanics[i].contract.yearlyWages;
                n++;
            }
            return n > 0 ? total / n : 250000;
        }

        public static List<CandidateSpec> GenerateCandidates(Team team)
        {
            List<CandidateSpec> list = new List<CandidateSpec>();
            long baseWage = Math.Max(50000, AverageMechanicWage(team));
            // Budget, mid-table, elite.
            float[][] tiers = { new[] { 4f, 8f }, new[] { 9f, 13f }, new[] { 14f, 18f } };
            Array specialties = Enum.GetValues(typeof(Specialty));
            for (int t = 0; t < tiers.Length; t++)
            {
                Person temp = new Person();
                App.instance.regenManager.GeneratePersonalDetails(temp, null);
                CandidateSpec c = new CandidateSpec
                {
                    first = temp.firstName,
                    last = temp.lastName,
                    countryKey = temp.nationality != null ? temp.nationality.countryKey : "",
                    gender = (int)temp.gender,
                    dobTicks = temp.dateOfBirth.Ticks,
                    planning = Mathf.Round(Range(tiers[t][0], tiers[t][1])),
                    reactions = Mathf.Round(Range(tiers[t][0], tiers[t][1])),
                    specialty = (Specialty)specialties.GetValue(sRandom.Next(1, specialties.Length))
                };
                c.potential = Mathf.Min(20f, Mathf.Max(c.planning, c.reactions) + Mathf.Round(Range(0f, 5f)));
                c.yearlyWage = (int)GameUtility.RoundCurrency((long)(baseWage * (0.6f + 0.35f * c.Stars)));
                list.Add(c);
            }
            return list;
        }

        // ------------------------------------------------------------------
        // Hire / fire
        // ------------------------------------------------------------------

        public static StrategyDirector Hire(Team team, CandidateSpec spec)
        {
            if (FindDirector(team) != null)
            {
                return null;
            }
            StrategyDirector d = Game.instance.entityManager.CreateEntity<StrategyDirector>();
            Nationality nat = string.IsNullOrEmpty(spec.countryKey) ? null : Nationality.GetNationalityByName(spec.countryKey);
            d.gender = (Person.Gender)spec.gender;
            App.instance.regenManager.GeneratePersonalDetails(d, null, null, true, nat);
            d.SetName(spec.first, spec.last);
            d.gender = (Person.Gender)spec.gender;
            if (nat != null)
            {
                d.nationality = nat;
            }
            d.dateOfBirth = new DateTime(spec.dobTicks);
            d.stats.planning = spec.planning;
            d.stats.reactions = spec.reactions;
            d.stats.potential = spec.potential;
            d.specialty = spec.specialty;
            d.contract.job = Contract.Job.Unemployed;

            ContractManagerTeam cm = team.contractManager;
            if (cm.GetFreeSlot(Contract.Job.Staff) == null)
            {
                cm.AddEmployeeSlot(Contract.Job.Staff, null);
            }
            ContractPerson contract = new ContractPerson();
            contract.employeer = team;
            contract.job = Contract.Job.Staff;
            contract.startDate = Game.instance.time.now;
            contract.endDate = contract.startDate.AddYears(2);
            contract.yearlyWages = spec.yearlyWage;
            cm.HireNewPerson(contract, d);
            Main.Log(string.Format("Hired strategy director {0} ({1}, {2}).", d.name, StrategyDirector.StarText(d.stats.GetAbility()), StrategyDirector.SpecialtyName(d.specialty)));
            return d;
        }

        public static void Fire(Team team, StrategyDirector d, string reason)
        {
            if (team == null || d == null || team.contractManager.GetSlotForPerson(d) == null)
            {
                return;
            }
            team.contractManager.FirePerson(d, Contract.ContractTerminationType.FiredByPlayer);
            Main.Log("Strategy director " + d.name + " left the team (" + reason + ").");
        }

        // ------------------------------------------------------------------
        // Renewal (0.5.0)
        // ------------------------------------------------------------------

        /// <summary>The yearly wage the director asks to extend: the hiring formula at today's stars, give or take 10%.</summary>
        public static int RenewalAsk(Team team, StrategyDirector d)
        {
            long baseWage = Math.Max(50000, AverageMechanicWage(team));
            float stars = d.stats.GetAbility();
            float swing = 0.9f + 0.2f * (float)sRandom.NextDouble();
            long ask = (long)(baseWage * (0.6f + 0.35f * stars) * swing);
            // Never a pay cut of more than 10% for someone you want to keep.
            ask = Math.Max(ask, (long)(d.contract.yearlyWages * 0.9f));
            return (int)GameUtility.RoundCurrency(ask);
        }

        public static void Renew(Team team, StrategyDirector d, int years, int yearlyWage)
        {
            if (team == null || d == null)
            {
                return;
            }
            DateTime from = d.contract.endDate > Game.instance.time.now ? d.contract.endDate : Game.instance.time.now;
            d.contract.endDate = from.AddYears(years);
            d.contract.yearlyWages = yearlyWage;
            d.renewalOfferSentFor = d.contract.endDate; // no new offer until the next contract nears its end
            Main.Log(string.Format("RENEWED: strategy director {0} for {1} year(s) at {2}/year; contract now ends {3:d MMM yyyy}.",
                d.name, years, GameUtility.GetCurrencyString(yearlyWage), d.contract.endDate));
        }

        public static void SendRenewalMail(Team team, StrategyDirector d)
        {
            int ask = RenewalAsk(team, d);
            int days = Math.Max(0, (int)(d.contract.endDate - Game.instance.time.now).TotalDays);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format("Boss, my contract ends on {0:d MMMM yyyy}, in {1} days. I'd like to stay on.", d.contract.endDate, days));
            sb.AppendLine();
            sb.AppendLine(string.Format("I'm asking {0} a year (currently {1}). One year or two, your call.", GameUtility.GetCurrencyString(ask), GameUtility.GetCurrencyString(d.contract.yearlyWages)));
            sb.AppendLine();
            sb.AppendLine("If we don't agree terms, I'll leave when the contract runs out. You can also renew from my card on the Staff screen.");
            List<KeyValuePair<string, string>> buttons = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("RENEW|1|" + ask + "|" + d.contract.endDate.Ticks, "Renew 1 year"),
                new KeyValuePair<string, string>("RENEW|2|" + ask + "|" + d.contract.endDate.Ticks, "Renew 2 years"),
                new KeyValuePair<string, string>("LETGO|" + d.contract.endDate.Ticks, "Let them go")
            };
            PostMail(team.teamAssistant ?? (Person)team.chairman, d.shortName + " — contract renewal", sb.ToString(), buttons, Message.Group.Staff);
            d.renewalOfferSentFor = d.contract.endDate;
            Main.Log(string.Format("Sent renewal offer from {0} (contract ends {1:d MMM yyyy}, asking {2}/year).", d.name, d.contract.endDate, GameUtility.GetCurrencyString(ask)));
        }

        // ------------------------------------------------------------------
        // Mail plumbing
        // ------------------------------------------------------------------

        public static Message PostMail(Person sender, string title, string body, List<KeyValuePair<string, string>> buttons, Message.Group group)
        {
            Message message = MessageManager.CreateMessage();
            message.SetMessageTextFields(new List<string> { body }, title);
            message.showNotification = true;
            message.portraitId = 0;
            if (sender != null)
            {
                message.SetSender(sender);
            }
            message.SetReceiver(Game.instance.player);
            message.deliverDate = Game.instance.time.now;
            message.deliverStateGroup = GameState.Group.Frontend;
            message.SetGroupType(group);
            if (buttons != null && buttons.Count > 0)
            {
                DialogRule rule = new DialogRule();
                rule.localisationID = RuleTag;
                rule.databaseIndex = RuleTag;
                for (int i = 0; i < buttons.Count; i++)
                {
                    // Labels travel with the ID ("ID~Label") so they survive save/load.
                    rule.userData.Add(new DialogCriteria("MailButton", ButtonPrefix + buttons[i].Key + "~" + buttons[i].Value));
                }
                message.buttonsRule = rule;
            }
            Game.instance.messageManager.PostMessage(message);
            return message;
        }

        public static bool IsOurMessage(Message m)
        {
            return m != null && m.buttonsRule != null && m.buttonsRule.localisationID == RuleTag;
        }

        public static int CountOurButtons(Message m)
        {
            int n = 0;
            if (IsOurMessage(m))
            {
                for (int i = 0; i < m.buttonsRule.userData.Count; i++)
                {
                    if (m.buttonsRule.userData[i].mType.Equals("mailbutton", StringComparison.OrdinalIgnoreCase))
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        /// <summary>Handle a button press. Returns the reply text shown under the mail.</summary>
        public static string HandleButton(Message message, string payload)
        {
            string[] f = payload.Split('|');
            Team team = PlayerTeam;
            switch (f[0])
            {
                case "HIRE":
                {
                    if (team == null)
                    {
                        return "You aren't running a team right now.";
                    }
                    if (FindDirector(team) != null)
                    {
                        return "You already have a strategy director. Let them go first (Ctrl+F10 → Strategy Director).";
                    }
                    CandidateSpec spec = CandidateSpec.Decode(f);
                    StrategyDirector d = Hire(team, spec);
                    return d != null
                        ? string.Format("{0} has signed a two-year deal at {1}/year. Their first briefing arrives three days before the next race.", d.name, GameUtility.GetCurrencyString(spec.yearlyWage))
                        : "The deal fell through.";
                }
                case "PLAN":
                {
                    StrategyDirector d = Current;
                    if (d == null)
                    {
                        return "No strategy director to brief.";
                    }
                    d.chosenPlan = (PlanType)int.Parse(f[1]);
                    d.chosenPlanEventDate = new DateTime(long.Parse(f[2]));
                    Main.Log("Plan chosen for the next race: " + StrategyDirector.PlanName(d.chosenPlan));
                    return d.chosenPlan == PlanType.Manual
                        ? "Understood — you're on the pit wall this weekend. I'll stay out of the way."
                        : string.Format("Locked in: {0}. I'll take the cars at the start of the race. Take one back any time with Ctrl+Shift+1 / Ctrl+Shift+2.", StrategyDirector.PlanName(d.chosenPlan));
                }
                case "RENEW":
                {
                    StrategyDirector d = Current;
                    if (d == null)
                    {
                        return "They've already left the team.";
                    }
                    if (f.Length > 3 && d.contract.endDate.Ticks != long.Parse(f[3]))
                    {
                        return "That offer is out of date: the contract has already changed.";
                    }
                    int years = int.Parse(f[1]);
                    Renew(team, d, years, int.Parse(f[2]));
                    return string.Format("Done. {0} is with us until {1:d MMMM yyyy}.", d.name, d.contract.endDate);
                }
                case "LETGO":
                {
                    StrategyDirector d = Current;
                    return d == null ? "They've already left." : string.Format("Understood. {0} will leave when the contract ends on {1:d MMMM yyyy}. You can still renew from the Staff screen until then.", d.name, d.contract.endDate);
                }
                case "DISMISS":
                    return "Noted.";
            }
            return "";
        }

        // ------------------------------------------------------------------
        // Applicants mail
        // ------------------------------------------------------------------

        public static void SendApplicantsMail(Team team)
        {
            List<CandidateSpec> candidates = GenerateCandidates(team);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Boss, we've had interest from three strategy directors. Whoever we sign runs race strategy from the pit wall: they brief you before each race, you choose the plan, and they call the race.");
            sb.AppendLine();
            List<KeyValuePair<string, string>> buttons = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < candidates.Count; i++)
            {
                CandidateSpec c = candidates[i];
                Nationality nat = string.IsNullOrEmpty(c.countryKey) ? null : Nationality.GetNationalityByName(c.countryKey);
                int age = Game.instance.time.now.Year - new DateTime(c.dobTicks).Year;
                sb.AppendLine(string.Format("<b>{0} {1}</b> — {2}  ({3}, age {4})", c.first, c.last, StrategyDirector.StarText(c.Stars), nat != null ? nat.localisedNationality : "—", age));
                sb.AppendLine(string.Format("   Planning {0:0}/20, Reactions {1:0}/20. Specialty: {2} — {3}.", c.planning, c.reactions, StrategyDirector.SpecialtyName(c.specialty), StrategyDirector.SpecialtyBlurb(c.specialty)));
                sb.AppendLine(string.Format("   Asking {0} a year, two-year deal.", GameUtility.GetCurrencyString(c.yearlyWage)));
                sb.AppendLine();
                buttons.Add(new KeyValuePair<string, string>(c.Encode(), "Hire " + c.last));
            }
            buttons.Add(new KeyValuePair<string, string>("DISMISS", "Not now"));
            Person sender = team.teamAssistant ?? (Person)team.chairman;
            PostMail(sender, "Strategy director applicants", sb.ToString(), buttons, Message.Group.Staff);
            Main.Settings.MarkOffered(Game.instance.player.id.ToString(), Game.instance.time.now);
            Main.Log("Sent strategy director applicants mail.");
        }

        // ------------------------------------------------------------------
        // Briefing mail
        // ------------------------------------------------------------------

        public static void SendBriefing(Team team, StrategyDirector d)
        {
            Championship championship = team.championship;
            RaceEventDetails ev = championship.GetCurrentEventDetails();
            if (ev == null || d.lastBriefingEventDate == ev.eventDate)
            {
                return;
            }
            d.lastBriefingEventDate = ev.eventDate;

            Circuit circuit = ev.circuit;
            int laps = 0;
            try
            {
                laps = DesignDataManager.CalculateRaceLapCount(championship, circuit.trackLengthMiles);
            }
            catch (Exception)
            {
            }
            string wear = circuit.GetTyreWearLocalised();
            // The director's read of the race-day weather: imperfect, by Logistics Centre level and skill.
            string forecast = "no forecast available yet";
            string accuracy = null;
            bool rainExpected = false;
            bool wetStart = false;
            try
            {
                SessionWeatherDetails weather = ev.GetRaceSession().sessionWeather;
                List<Forecast.Phase> phases = Forecast.Phases(weather, laps);
                if (phases.Count > 0)
                {
                    bool anyWet = phases.Exists(ph => ph.tread != TyreSet.Tread.Slick);
                    bool missed = anyWet && sRandom.NextDouble() < Forecast.MissChance(d, team);
                    if (missed)
                    {
                        forecast = "dry all race";
                    }
                    else
                    {
                        forecast = Forecast.Describe(phases, laps, Forecast.RollError(d, team, laps));
                        rainExpected = anyWet;
                        wetStart = phases[0].tread != TyreSet.Tread.Slick;
                    }
                    accuracy = Forecast.Accuracy(d, team, laps);
                }
            }
            catch (Exception)
            {
            }

            PlanType recommended = Recommend(circuit, forecast, d);
            d.chosenPlan = recommended;
            d.chosenPlanEventDate = ev.eventDate;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format("Race brief for {0}: {1} laps, tyre wear {2}.", circuit.locationName, laps > 0 ? laps.ToString() : "?", wear));
            sb.AppendLine(string.Format("Weather read: {0}{1}.", forecast, accuracy != null ? " — " + accuracy : ""));
            if (wetStart)
            {
                sb.AppendLine("We'll start on whatever the track needs on the grid; the plan below covers the dry running.");
            }
            else if (rainExpected)
            {
                sb.AppendLine("With rain coming, I'll plan the dry stint to end when it arrives rather than for the full distance — expect softer, shorter running early.");
            }
            sb.AppendLine("I'll re-check the sky on the grid and adjust the start tyre if the track needs it.");
            sb.AppendLine();
            sb.AppendLine("<b>Fastest on paper</b> — whatever tyre sequence the numbers say is quickest overall. Balanced pace.");
            sb.AppendLine("<b>Fewest stops</b> — long stints on harder rubber, look after the tyres, track position over pace.");
            sb.AppendLine("<b>Attack</b> — softer tyres and an extra stop if it pays; push hard all race.");
            if (d.specialty == Specialty.Gambler)
            {
                sb.AppendLine("<b>Gamble</b> — start on the hard tyre, run long and pounce on a safety car to stop for free.");
            }
            sb.AppendLine();
            sb.AppendLine(string.Format("My call: <b>{0}</b>. If you don't reply, that's what we'll run.", StrategyDirector.PlanName(recommended)));

            for (int i = 0; i < 2; i++)
            {
                Driver driver = team.GetDriver(i);
                if (driver != null && driver.personalityTraitController.HasSpecialCase(PersonalityTrait.SpecialCaseType.TurnOffStrategy))
                {
                    sb.AppendLine();
                    sb.AppendLine(string.Format("Heads up: {0} is in an 'ignores your strategy' mood. Whatever we decide, expect them to freelance.", driver.name));
                }
            }

            List<KeyValuePair<string, string>> buttons = new List<KeyValuePair<string, string>>();
            long t = ev.eventDate.Ticks;
            buttons.Add(new KeyValuePair<string, string>("PLAN|" + (int)PlanType.Fastest + "|" + t, "Fastest on paper"));
            buttons.Add(new KeyValuePair<string, string>("PLAN|" + (int)PlanType.FewestStops + "|" + t, "Fewest stops"));
            buttons.Add(new KeyValuePair<string, string>("PLAN|" + (int)PlanType.Attack + "|" + t, "Attack"));
            if (d.specialty == Specialty.Gambler)
            {
                buttons.Add(new KeyValuePair<string, string>("PLAN|" + (int)PlanType.Gamble + "|" + t, "Gamble"));
            }
            buttons.Add(new KeyValuePair<string, string>("PLAN|" + (int)PlanType.Manual + "|" + t, "I'll run it myself"));
            PostMail(d, "Race strategy brief: " + circuit.locationName, sb.ToString(), buttons, Message.Group.Staff);
            Main.Log("Sent race briefing for " + circuit.locationName + ".");
        }

        private static PlanType Recommend(Circuit circuit, string forecast, StrategyDirector d)
        {
            if (d.specialty == Specialty.Gambler && sRandom.NextDouble() < 0.5)
            {
                return PlanType.Gamble;
            }
            if (d.specialty == Specialty.SafetyFirst)
            {
                return PlanType.FewestStops;
            }
            switch (circuit.tyreWearRate)
            {
                case Circuit.Rate.VeryLow:
                case Circuit.Rate.Low:
                    return PlanType.FewestStops;
                default:
                    return PlanType.Fastest;
            }
        }

        // ------------------------------------------------------------------
        // Daily housekeeping
        // ------------------------------------------------------------------

        public static void OnDayEnd()
        {
            Team team = PlayerTeam;
            if (team == null || !Main.Enabled)
            {
                return;
            }
            StrategyDirector d = FindDirector(team);
            if (d != null)
            {
                if (d.contract.endDate <= Game.instance.time.now)
                {
                    string name = d.name;
                    team.contractManager.FirePerson(d, Contract.ContractTerminationType.ContractElapsed);
                    Main.Log("Strategy director " + name + " left the team (contract expired, not renewed).");
                    PostMail(team.teamAssistant ?? (Person)team.chairman, "Strategy director contract ended",
                        name + "'s contract has run out and they've moved on. The cars are yours until you hire someone new: use Find on the Strategy Director card (Staff screen), or wait for applicants to get in touch.", null, Message.Group.Staff);
                    Main.Settings.MarkOffered(Game.instance.player.id.ToString(), DateTime.MinValue);
                }
                else if (Main.Settings.RenewalNoticeDays > 0 && d.renewalOfferSentFor != d.contract.endDate
                    && (d.contract.endDate - Game.instance.time.now).TotalDays <= Main.Settings.RenewalNoticeDays)
                {
                    SendRenewalMail(team, d);
                }
                return;
            }
            if (Main.Settings.AutoOfferApplicants)
            {
                DateTime last = Main.Settings.LastOffered(Game.instance.player.id.ToString());
                if ((Game.instance.time.now - last).TotalDays >= Main.Settings.DaysBetweenOffers)
                {
                    SendApplicantsMail(team);
                }
            }
        }
    }

    // ----------------------------------------------------------------------
    // Harmony hooks for this layer
    // ----------------------------------------------------------------------

    [HarmonyPatch(typeof(DialogSystem), "OnDayEndMessages")]
    internal static class DayEndPatch
    {
        private static void Postfix()
        {
            try
            {
                DirectorService.OnDayEnd();
            }
            catch (Exception e)
            {
                Main.Error("Day-end check failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(DialogSystem), "OnThreeDaysToEvent")]
    internal static class BriefingPatch
    {
        private static void Postfix()
        {
            if (!Main.Enabled)
            {
                return;
            }
            try
            {
                Team team = DirectorService.PlayerTeam;
                StrategyDirector d = DirectorService.FindDirector(team);
                if (d != null)
                {
                    DirectorService.SendBriefing(team, d);
                }
            }
            catch (Exception e)
            {
                Main.Error("Briefing failed: " + e);
            }
        }
    }

    /// <summary>Wages: the game doesn't charge for Job.Staff, so we add a line to staff costs.</summary>
    [HarmonyPatch(typeof(TeamFinanceController), "GetEventTransactions")]
    internal static class WagesPatch
    {
        private static void Postfix(TeamFinanceController __instance, Transaction.Group inGroup, List<Transaction> __result)
        {
            if (inGroup != Transaction.Group.HQUpkeepAndStaff || __result == null)
            {
                return;
            }
            try
            {
                Team team = Traverse.Create(__instance).Field("mTeam").GetValue<Team>();
                StrategyDirector d = DirectorService.FindDirector(team);
                if (d != null)
                {
                    long cost = d.contract.perRaceCost;
                    if (cost > 0)
                    {
                        __result.Add(new Transaction(Transaction.Group.HQUpkeepAndStaff, Transaction.Type.Debit, cost, "Strategy Director wages (" + d.shortName + ")"));
                    }
                }
            }
            catch (Exception e)
            {
                Main.Error("Wages patch failed: " + e);
            }
        }
    }

    /// <summary>Make sure the mail has enough buttons for our options (clone the first if not).</summary>
    [HarmonyPatch(typeof(UIMailMessageBody), "SetupButtons")]
    internal static class MailButtonsCountPatch
    {
        private static void Prefix(UIMailMessageBody __instance)
        {
            try
            {
                Message m = Traverse.Create(__instance).Field("mMessage").GetValue<Message>();
                int needed = DirectorService.CountOurButtons(m);
                List<Button> buttons = __instance.buttons;
                if (needed == 0 || buttons == null || buttons.Count == 0)
                {
                    return;
                }
                while (buttons.Count < needed)
                {
                    Button template = buttons[buttons.Count - 1];
                    Button clone = UnityEngine.Object.Instantiate(template);
                    clone.transform.SetParent(template.transform.parent, false);
                    buttons.Add(clone);
                }
            }
            catch (Exception e)
            {
                Main.Error("Mail button setup failed: " + e);
            }
        }
    }

    /// <summary>Our button IDs: set the label and wire the click to our handler.</summary>
    [HarmonyPatch(typeof(UIMailMessageBody), "AssignButton")]
    internal static class MailButtonAssignPatch
    {
        private static bool Prefix(UIMailMessageBody __instance, Message inMessage, Button inButton, string inButtonType)
        {
            if (inButtonType == null || !inButtonType.StartsWith(DirectorService.ButtonPrefix))
            {
                return true;
            }
            string rest = inButtonType.Substring(DirectorService.ButtonPrefix.Length);
            int tilde = rest.LastIndexOf('~');
            string payload = tilde >= 0 ? rest.Substring(0, tilde) : rest;
            string label = tilde >= 0 ? rest.Substring(tilde + 1) : payload;

            inButton.onClick.RemoveAllListeners();
            TextMeshProUGUI text = inButton.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
            {
                text.text = label;
            }
            inButton.onClick.AddListener(delegate
            {
                try
                {
                    scSoundManager.Instance.PlaySound(SoundID.Button_Select);
                    string reply = DirectorService.HandleButton(inMessage, payload);
                    Traverse body = Traverse.Create(__instance);
                    body.Method("RespondedToMessage").GetValue();
                    TextDynamicData data = new TextDynamicData();
                    data.SetMessageTextFields(reply);
                    inMessage.messageResponseData = data;
                    body.Method("Refresh").GetValue();
                }
                catch (Exception e)
                {
                    Main.Error("Mail button failed: " + e);
                }
            });
            return false;
        }
    }
}
