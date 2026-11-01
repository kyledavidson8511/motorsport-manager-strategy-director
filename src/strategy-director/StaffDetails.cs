using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StrategyDirectorMod
{
    /// <summary>
    /// 0.5.3: the director on the game's own Staff Details screen (reached from her card's Details
    /// button or her tab in the bottom Staff bar). The screen is generic; we supply her stats,
    /// her job title, and our Fire / Renew / Race Plan handlers in place of the vanilla ones,
    /// which only understand drivers, designers and mechanics.
    /// </summary>
    internal static class StaffDetails
    {
        public static bool IsDirector(Person p)
        {
            return p is StrategyDirector;
        }

        public static void FixPersonInfo(UIStaffDetailsScreenPersonInfoWidget w, StrategyDirector d)
        {
            if (w == null || d == null) return;
            try
            {
                if (w.jobTitle != null) w.jobTitle.text = "Strategy Director";
                if (w.contractJobTitle != null) w.contractJobTitle.text = "Strategy Director";
                bool inSession = App.instance.gameStateManager.currentState.group != GameState.Group.Frontend;

                SetButton(w.negotiateContract, true, !inSession, () => { StaffCard.AfterChange = () => w.Setup(d); StaffCard.RenewFlow(); });
                if (w.contractButtonLabel != null) w.contractButtonLabel.text = "Renew Contract";

                SetButton(w.compare, true, true, StaffCard.RacePlanFlow);
                TextMeshProUGUI cmp = w.compare != null ? w.compare.GetComponentInChildren<TextMeshProUGUI>(true) : null;
                if (cmp != null)
                {
                    foreach (UILocaliseLabel l in cmp.GetComponents<UILocaliseLabel>()) UnityEngine.Object.Destroy(l);
                    cmp.text = "Race Plan";
                }

                SetButton(w.fire, true, !inSession, () => { StaffCard.AfterChange = () => UIManager.instance.ChangeScreen("StaffScreen"); StaffCard.FireFlow(); });

                // Arrows: left to the second mechanic, right to the Lead Designer (the vanilla cycle is unchanged).
                SetButton(w.leftButton, true, true, () => GoToStaff(2));
                SetButton(w.rightButton, true, true, () => GoToStaff(0));
            }
            catch (Exception e)
            {
                Main.Error("Staff Details (director) setup failed: " + e.Message);
            }
        }

        private static void SetButton(Button b, bool show, bool interactable, UnityEngine.Events.UnityAction action)
        {
            if (b == null) return;
            GameUtility.SetActive(b.gameObject, show);
            b.interactable = interactable;
            b.onClick.RemoveAllListeners();
            b.onClick.AddListener(action);
        }

        private static void GoToStaff(int index)
        {
            Team team = DirectorService.PlayerTeam;
            if (team == null) return;
            if (index == 0)
            {
                UIManager.instance.ChangeScreen("StaffDetailsScreen", team.contractManager.GetPersonOnJob(Contract.Job.EngineerLead));
                return;
            }
            List<Person> mechanics = team.contractManager.GetAllPeopleOnJob(Contract.Job.Mechanic);
            if (mechanics.Count >= index)
            {
                UIManager.instance.ChangeScreen("StaffDetailsScreen", mechanics[index - 1]);
            }
        }
    }

    /// <summary>Her stats on the Details screen (the dictionary is empty for unknown stat types).</summary>
    [HarmonyPatch(typeof(PersonStats), "GetStatDictionary")]
    internal static class DirectorStatDictionaryPatch
    {
        private static void Postfix(PersonStats inPersonStats, Dictionary<string, float> __result)
        {
            DirectorStats ds = inPersonStats as DirectorStats;
            if (ds == null || __result == null || __result.Count > 0)
            {
                return;
            }
            __result["Planning"] = ds.planning;
            __result["Reactions"] = ds.reactions;
            __result["Potential"] = Mathf.Max(ds.potential, (ds.planning + ds.reactions) / 2f);
        }
    }

    [HarmonyPatch(typeof(UIStaffDetailsScreenPersonStatsWidget), "Setup")]
    internal static class DirectorStatsTitlePatch
    {
        private static void Postfix(UIStaffDetailsScreenPersonStatsWidget __instance, Person inPerson)
        {
            if (StaffDetails.IsDirector(inPerson) && __instance.title != null)
            {
                __instance.title.text = "Strategy Director Stats";
            }
        }
    }

    [HarmonyPatch(typeof(UIStaffDetailsScreenPersonInfoWidget), "Setup")]
    internal static class DirectorPersonInfoPatch
    {
        private static Exception Finalizer(Exception __exception, UIStaffDetailsScreenPersonInfoWidget __instance, Person inPerson)
        {
            StrategyDirector d = inPerson as StrategyDirector;
            if (d == null)
            {
                return __exception;
            }
            if (__exception != null)
            {
                Main.Error("Staff Details info for the director threw (handled): " + __exception.Message);
            }
            StaffDetails.FixPersonInfo(__instance, d);
            return null;
        }
    }

    /// <summary>If anything in the screen's own setup trips over the director, keep going instead of breaking the screen.</summary>
    [HarmonyPatch(typeof(StaffDetailsScreen), "OnEnter")]
    internal static class DirectorDetailsScreenPatch
    {
        private static Exception Finalizer(Exception __exception, StaffDetailsScreen __instance)
        {
            if (__exception == null || !StaffDetails.IsDirector(__instance.person))
            {
                return __exception;
            }
            Main.Error("Staff Details screen for the director threw (handled): " + __exception.Message);
            try
            {
                __instance.careerWidget.Setup(__instance.person);
            }
            catch (Exception)
            {
            }
            try
            {
                __instance.personInfoWidget.Setup(__instance.person);
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
