using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace StrategyDirectorMod
{
    public class Settings : UnityModManager.ModSettings
    {
        public bool AutoOfferApplicants = true;
        public int DaysBetweenOffers = 60;
        public bool ShowOverlay = true;
        public bool LogEvents = true;
        public bool RunPractice = true;
        public bool RunQualifying = true;
        public bool ReviewAfterQualifying = true;
        public bool RefineSetups = true;
        public bool TimedQualifyingRuns = true;
        public bool StretchForTrackPosition = true;
        public bool FixDoubleClutch = true;
        public bool PitUnderSafetyCar = true;
        public bool SaveLogCopies = true;
        public bool ShowStaffCard = true;
        public bool DetailsScreen = true;
        public int RenewalNoticeDays = 60;
        public string CopyLogsToFolder = @"C:\Users\kyled\Desktop\Claude Cowork\projects\motorsport-manager-mods\inputs\logs";

        // "playerId=ticks" pairs: when each career last got an applicants mail.
        public List<string> OfferedLog = new List<string>();

        public DateTime LastOffered(string playerId)
        {
            foreach (string entry in OfferedLog)
            {
                int eq = entry.IndexOf('=');
                if (eq > 0 && entry.Substring(0, eq) == playerId)
                {
                    long ticks;
                    if (long.TryParse(entry.Substring(eq + 1), out ticks))
                    {
                        return new DateTime(ticks);
                    }
                }
            }
            return DateTime.MinValue;
        }

        public void MarkOffered(string playerId, DateTime when)
        {
            OfferedLog.RemoveAll(e => e.StartsWith(playerId + "="));
            OfferedLog.Add(playerId + "=" + when.Ticks);
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }

    public static class Main
    {
        public static bool Enabled;
        public static Settings Settings;
        public static UnityModManager.ModEntry Entry;
        private static Harmony sHarmony;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Entry = modEntry;
            Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
            // 0.4.4: logs moved to inputs\logs in the Cowork project.
            if (Settings.CopyLogsToFolder != null && Settings.CopyLogsToFolder.TrimEnd('\\').EndsWith(@"motorsport-manager-mods\inputs"))
            {
                Settings.CopyLogsToFolder = Settings.CopyLogsToFolder.TrimEnd('\\') + @"\logs";
            }
            modEntry.OnToggle = (e, on) => { Enabled = on; return true; };
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = e => Settings.Save(e);
            sHarmony = new Harmony(modEntry.Info.Id);
            sHarmony.PatchAll(Assembly.GetExecutingAssembly());
            Enabled = true;

            GameObject go = new GameObject("StrategyDirectorOverlay");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Overlay>();

            modEntry.Logger.Log("Strategy Director loaded.");
            return true;
        }

        public static void Log(string message)
        {
            if (Settings != null && Settings.LogEvents && Entry != null)
            {
                Entry.Logger.Log(message);
            }
        }

        public static void Error(string message)
        {
            if (Entry != null)
            {
                Entry.Logger.Error(message);
            }
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            Team team = DirectorService.PlayerTeam;
            StrategyDirector d = DirectorService.FindDirector(team);

            GUILayout.Label("<b>Strategy Director</b>");
            if (team == null)
            {
                GUILayout.Label("Load a career to manage your strategy director.");
            }
            else if (d == null)
            {
                GUILayout.Label("No strategy director on staff.");
                if (GUILayout.Button("Send me applicants now", GUILayout.Width(220)))
                {
                    DirectorService.SendApplicantsMail(team);
                }
            }
            else
            {
                GUILayout.Label(string.Format("{0} — {1} ({2})  ·  Planning {3:0}/20, Reactions {4:0}/20", d.name, StrategyDirector.StarText(d.stats.GetAbility()),
                    StrategyDirector.SpecialtyName(d.specialty), d.stats.planning, d.stats.reactions));
                GUILayout.Label(string.Format("Wage {0}/year ({1}/race) · contract ends {2:d MMM yyyy} · {3} race(s) run",
                    GameUtility.GetCurrencyString(d.contract.yearlyWages), GameUtility.GetCurrencyString(d.contract.perRaceCost), d.contract.endDate, d.racesRun));
                DateTime ev = default(DateTime);
                try { ev = team.championship.GetCurrentEventDetails().eventDate; } catch (Exception) { }
                GUILayout.Label("Plan for the next/current race (click to change per car):");
                for (int c = 0; c < 2; c++)
                {
                    Driver drv = team.GetDriver(c);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(string.Format("   Car {0} ({1}): {2}", c + 1, drv != null ? drv.lastName : "?", StrategyDirector.PlanName(d.PlanForDriver(drv, c, ev))), GUILayout.Width(330));
                    foreach (PlanType p in new[] { PlanType.Fastest, PlanType.FewestStops, PlanType.Attack, PlanType.Gamble, PlanType.Manual })
                    {
                        string label = p == PlanType.Fastest ? "Fastest" : p == PlanType.FewestStops ? "Fewest" : p == PlanType.Attack ? "Attack" : p == PlanType.Gamble ? "Offset" : "Manual";
                        if (GUILayout.Button(label, GUILayout.Width(70)))
                        {
                            d.SetDriverPlan(drv, c, p, ev);
                            Log(string.Format("Plan for car {0} set to {1} by you.", c + 1, StrategyDirector.PlanName(p)));
                        }
                    }
                    GUILayout.EndHorizontal();
                }
                for (int i = 0; i < 2; i++)
                {
                    Driver driver = team.GetDriver(i);
                    if (driver == null) continue;
                    DirectorRelationship rel = d.GetRelationship(driver);
                    string bonus = rel.InSync ? "IN SYNC (calls always on time)" : rel.HasTrust ? "TRUST (+5% call accuracy)" : "building";
                    GUILayout.Label(string.Format("   Relationship with {0}: {1:0}% over {2} race(s) — {3}", driver.name, rel.amount, rel.racesTogether, bonus));
                }
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Resend this race's briefing", GUILayout.Width(220)))
                {
                    d.lastBriefingEventDate = default(DateTime);
                    DirectorService.SendBriefing(team, d);
                }
                if (GUILayout.Button("Let the director go", GUILayout.Width(180)))
                {
                    DirectorService.Fire(team, d, "released by you");
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            GUILayout.Label("During practice, qualifying or the race: <b>Ctrl+Shift+1</b> / <b>Ctrl+Shift+2</b> takes car 1 / car 2 back from the director for that session (press again to hand it back).");
            Settings.RunPractice = GUILayout.Toggle(Settings.RunPractice, " Director runs practice (qualifying-trim runs, then the race compounds)");
            Settings.RunQualifying = GUILayout.Toggle(Settings.RunQualifying, " Director runs qualifying (qualifies on the race start tyre where it's locked in)");
            Settings.TimedQualifyingRuns = GUILayout.Toggle(Settings.TimedQualifyingRuns, " Qualifying: early banker lap, then a final run timed to start its flying lap before the flag (fresh tyres both runs)");
            Settings.StretchForTrackPosition = GUILayout.Toggle(Settings.StretchForTrackPosition, " Race: stretch the tyres to hold track position when running top 3 or 3+ places above the forecast");
            Settings.PitUnderSafetyCar = GUILayout.Toggle(Settings.PitUnderSafetyCar, " Race: under a safety car, take any stop the plan still owes (or fresh tyres if they're worn and 5+ laps remain)");
            Settings.FixDoubleClutch = GUILayout.Toggle(Settings.FixDoubleClutch, " Practice/qualifying: stop the car turning back into the garage when it's sent out (double-clutch fix)");
            Settings.ReviewAfterQualifying = GUILayout.Toggle(Settings.ReviewAfterQualifying, " Director reviews the race plan after qualifying");
            Settings.RefineSetups = GUILayout.Toggle(Settings.RefineSetups, " Engineer refines the Auto Setup baseline between practice runs (driver feedback + mechanic)");
            Settings.ShowStaffCard = GUILayout.Toggle(Settings.ShowStaffCard, " Show the Strategy Director card on the Staff screen");
            Settings.ShowOverlay = GUILayout.Toggle(Settings.ShowOverlay, " Show the director status bar during sessions");
            Settings.AutoOfferApplicants = GUILayout.Toggle(Settings.AutoOfferApplicants, " Applicants get in touch automatically when you have no director");
            Settings.LogEvents = GUILayout.Toggle(Settings.LogEvents, " Log director decisions to the UMM log");
            Settings.SaveLogCopies = GUILayout.Toggle(Settings.SaveLogCopies, " Save a copy of the game log after every session and at quit (Mods\\StrategyDirector\\logs, plus the folder below)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("   Also copy logs to:", GUILayout.Width(140));
            Settings.CopyLogsToFolder = GUILayout.TextField(Settings.CopyLogsToFolder ?? "", GUILayout.Width(520));
            GUILayout.EndHorizontal();
        }
    }

    /// <summary>Race-time status bar and take-back hotkeys.</summary>
    public class Overlay : MonoBehaviour
    {
        private GUIStyle mStyle;

        private void OnApplicationQuit()
        {
            LogArchive.Save("quit");
        }

        private void Update()
        {
            if (!Main.Enabled || !RaceState.InDirectedSession())
            {
                return;
            }
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift)
            {
                return;
            }
            int car = Input.GetKeyDown(KeyCode.Alpha1) ? 0 : Input.GetKeyDown(KeyCode.Alpha2) ? 1 : -1;
            if (car < 0)
            {
                return;
            }
            try
            {
                RaceState.Ensure();
                RacingVehicle[] cars = Game.instance.vehicleManager.GetPlayerVehicles();
                if (cars == null || car >= cars.Length || cars[car] == null)
                {
                    return;
                }
                RacingVehicle v = cars[car];
                if (RaceState.TakenBack.Remove(v))
                {
                    Main.Log(string.Format("HANDOVER: {0}, {1} — car handed back to the director.", RaceState.DriverName(v), RaceState.LapText()));
                }
                else
                {
                    RaceState.TakenBack.Add(v);
                    Main.Log(string.Format("TAKE BACK: {0}, {1} — you have this car now.", RaceState.DriverName(v), RaceState.LapText()));
                }
            }
            catch (Exception e)
            {
                Main.Error("Hotkey failed: " + e);
            }
        }

        private void OnGUI()
        {
            if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShowOverlay || !RaceState.InDirectedSession())
            {
                return;
            }
            try
            {
                RaceState.Ensure();
                if (RaceState.Director == null)
                {
                    return;
                }
                if (mStyle == null)
                {
                    mStyle = new GUIStyle(GUI.skin.box);
                    mStyle.alignment = TextAnchor.MiddleLeft;
                    mStyle.fontSize = 13;
                    mStyle.richText = true;
                    mStyle.normal.textColor = Color.white;
                }
                string text = "<b>Strategy Director</b> " + RaceState.Director.shortName;
                RacingVehicle[] cars = Game.instance.vehicleManager.GetPlayerVehicles();
                for (int i = 0; cars != null && i < cars.Length; i++)
                {
                    RacingVehicle v = cars[i];
                    if (v == null) continue;
                    PlanType plan = RaceState.PlanFor(v);
                    string who = plan == PlanType.Manual || RaceState.TakenBack.Contains(v) || !RaceState.SessionAllowed() ? "YOU"
                        : RaceState.SessionType == SessionDetails.SessionType.Race && RaceState.IsRogue(v) ? "FREELANCING" : "DIRECTOR · " + StrategyDirector.PlanName(plan);
                    text += string.Format("   |   Car {0} ({1}): <b>{2}</b>", i + 1, v.driver != null ? v.driver.lastName : "?", who);
                }
                text += "   |   Ctrl+Shift+1/2 to take back";
                GUI.Box(new Rect(10, 10, 900, 26), text, mStyle);
            }
            catch (Exception)
            {
            }
        }
    }
}
