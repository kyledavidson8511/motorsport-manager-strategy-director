using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StrategyDirectorMod
{
    /// <summary>
    /// 0.5.0: the Strategy Director as a staff member on the Staff screen. A clone of the
    /// Head Designer card, retitled, with our own Details / Fire / Renew / Find handlers and
    /// relationship rows for both drivers. Placement comes from Mods\StrategyDirector\card-layout.txt
    /// (re-read every time the Staff screen opens, so it can be tuned without restarting), and
    /// the screen's layout is dumped to staff-screen-dump.txt the first time it opens.
    /// </summary>
    internal static class StaffCard
    {
        private static GameObject sCard;
        private static UIStaffPersonInfoWidget sWidget;
        private static TextMeshProUGUI sTitle;
        private static GameObject sRows;
        private static TextMeshProUGUI sRowsText;
        private static readonly List<Slider> sBars = new List<Slider>();
        private static StaffScreen sScreen;
        private static bool sDumped;
        private static bool sCardDumped;
        private static bool sShowBars = true;

        // ------------------------------------------------------------------
        // Layout file
        // ------------------------------------------------------------------

        private class Layout
        {
            public float offsetX = -456f;     // from the Head Designer card (card width 440 + gap): left of it
            public float height = 823f;       // compact card (the Designer card is 823 with its components panel)
            public float offsetY = 0f;
            public float scale = 1f;
            public float rowsX = 20f;         // relationship block, from the card's top-left
            public float rowsY = -470f;       // from the card's top: under the portrait/contract block
            public float rowsWidth = 400f;
            public float rowsHeight = 110f;
            public float fontSize = 0f;       // 0 = keep the cloned label's size
            public bool bars = true;
            public bool dump = false;
            public float sec1Y = 0f;          // relationship section 1, offset from where the mechanic card has it
            public float sec2Y = -175f;       // section 2 sits this far below section 1
            public bool textRows = true;      // fallback text rows (only if the sections can't be built)
        }

        private static string LayoutPath
        {
            get { return Path.Combine(Main.Entry.Path, "card-layout.txt"); }
        }

        private static Layout ReadLayout()
        {
            Layout l = new Layout();
            try
            {
                if (!File.Exists(LayoutPath))
                {
                    File.WriteAllText(LayoutPath,
                        "# Strategy Director card on the Staff screen. Edit, then leave and re-open the Staff screen.\r\n" +
                        "# Positions are in UI pixels relative to the Head Designer card (top-aligned). The game lays its columns out in a 1920-wide frame:\r\n" +
                        "# Designer x=-701, Mechanic x=-245, Mechanic 2 x=210, Contracts x=686, each ~440 wide. offsetX=-456 puts the card left of the Designer.\r\n" +
                        "offsetX = -456\r\noffsetY = 0\r\nheight = 823\r\nsec1Y = 0\r\nsec2Y = -175\r\nscale = 1\r\nrowsX = 20\r\nrowsY = -470\r\nrowsWidth = 400\r\nrowsHeight = 110\r\nfontSize = 0\r\nbars = true\r\ndump = false\r\n");
                }
                foreach (string raw in File.ReadAllLines(LayoutPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (v.Length == 0) continue;
                    float f;
                    bool isNum = float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
                    bool b = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    switch (k)
                    {
                        case "offsetx": if (isNum) l.offsetX = f; break;
                        case "height": if (isNum && f > 100f) l.height = f; break;
                        case "offsety": if (isNum) l.offsetY = f; break;
                        case "scale": if (isNum && f > 0.1f) l.scale = f; break;
                        case "rowsx": if (isNum) l.rowsX = f; break;
                        case "rowsy": if (isNum) l.rowsY = f; break;
                        case "rowswidth": if (isNum) l.rowsWidth = f; break;
                        case "rowsheight": if (isNum) l.rowsHeight = f; break;
                        case "fontsize": if (isNum) l.fontSize = f; break;
                        case "bars": l.bars = b; break;
                        case "dump": l.dump = b; break;
                        case "sec1y": if (isNum) l.sec1Y = f; break;
                        case "sec2y": if (isNum) l.sec2Y = f; break;
                        case "textrows": l.textRows = b; break;
                    }
                }
            }
            catch (Exception e)
            {
                Main.Error("Card layout file: " + e.Message);
            }
            return l;
        }

        // ------------------------------------------------------------------
        // Building and refreshing the card
        // ------------------------------------------------------------------

        public static void OnStaffScreen(StaffScreen screen)
        {
            if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShowStaffCard || screen == null || screen.headDesign == null)
            {
                if (sCard != null) sCard.SetActive(false);
                return;
            }
            sScreen = screen;
            Layout layout = ReadLayout();
            if (!sDumped || layout.dump)
            {
                sDumped = true;
                Dump(screen);
            }
            if (sCard == null)
            {
                Build(screen);
            }
            if (sCard == null)
            {
                return;
            }
            sCard.SetActive(true);
            Place(screen, layout);
            Refresh();
            DirectorTab.Ensure();
            if (layout.dump || !sCardDumped)
            {
                sCardDumped = true;
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Strategy Director card " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    DumpTree(sb, sCard.transform, 0, 8);
                    File.WriteAllText(Path.Combine(Main.Entry.Path, "staff-card-dump.txt"), sb.ToString());
                }
                catch (Exception e)
                {
                    Main.Error("Card dump failed: " + e.Message);
                }
            }
        }

        private static void Build(StaffScreen screen)
        {
            GameObject src = screen.headDesign.gameObject;
            // Clone while the source is inactive, so nothing in the copy runs (Awake/Update) until
            // it's been stripped of parts that only work with runtime wiring (0.5.1: the cloned
            // Known Components tooltip threw every frame, UITooltipTrigger.mTooltip is set at runtime).
            bool wasActive = src.activeSelf;
            src.SetActive(false);
            try
            {
                sCard = (GameObject)UnityEngine.Object.Instantiate(src);
            }
            finally
            {
                src.SetActive(wasActive);
            }
            sCard.name = "StrategyDirectorCard";
            sCard.transform.SetParent(src.transform.parent, false);
            Strip(sCard);
            // Activate before wiring: the widget's Awake adds the vanilla button listeners, which Wire() then replaces.
            sCard.SetActive(true);
            // Don't let the vanilla layout (if any) reflow around us; we place the card ourselves.
            LayoutElement le = sCard.GetComponent<LayoutElement>() ?? sCard.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            sWidget = sCard.GetComponent<UIStaffPersonInfoWidget>();

            Wire(sWidget.detailsButton, OpenDetails);
            Wire(sWidget.fireButton, OnFire);
            Wire(sWidget.compareButton, null);
            Wire(sWidget.negotiateButton, OnRenew);
            Wire(sWidget.findButton, OnFind);

            // Retitle: the header label is the TMP text that says "...Design..." in the clone.
            HashSet<TextMeshProUGUI> fields = new HashSet<TextMeshProUGUI> { sWidget.personName, sWidget.age, sWidget.costToBreakContract, sWidget.expires, sWidget.wage };
            foreach (TextMeshProUGUI t in sCard.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (fields.Contains(t) || t.text == null) continue;
                if (t.text.IndexOf("design", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sTitle = t;
                    break;
                }
            }
            if (sTitle != null)
            {
                DropLocaliser(sTitle.gameObject);
                sTitle.text = "Strategy Director";
            }
            // Compare becomes "Race Plan" (this weekend's plan per car), so the card has the same two-button row as the others.
            Wire(sWidget.compareButton, OnRacePlan);
            TextMeshProUGUI cmp = sWidget.compareButton != null ? sWidget.compareButton.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            if (cmp != null) { DropLocaliser(cmp.gameObject); cmp.text = "Race Plan"; }
            // The Negotiate button reads "Renew" on our card.
            BuildRows(screen);
            BuildRelationshipSections(screen);
            Main.Log("STAFF CARD: built (" + (sTitle != null ? "title found" : "no title label found") + ").");
        }

        private static void Strip(GameObject card)
        {
            int removed = 0;
            // The Designer's "Known Components" panel has no meaning for a strategy director.
            foreach (UIStaffComponentsWidget w in card.GetComponentsInChildren<UIStaffComponentsWidget>(true))
            {
                UnityEngine.Object.DestroyImmediate(w.gameObject);
                removed++;
            }
            foreach (UITooltipTrigger t in card.GetComponentsInChildren<UITooltipTrigger>(true)) { UnityEngine.Object.DestroyImmediate(t.gameObject); removed++; }
            foreach (UITooltipIcon t in card.GetComponentsInChildren<UITooltipIcon>(true)) { UnityEngine.Object.DestroyImmediate(t.gameObject); removed++; }
            foreach (MouseEvents m in card.GetComponentsInChildren<MouseEvents>(true)) { UnityEngine.Object.DestroyImmediate(m); removed++; }
            foreach (UIGridList g in card.GetComponentsInChildren<UIGridList>(true)) { UnityEngine.Object.DestroyImmediate(g); removed++; }
            Main.Log("STAFF CARD: stripped " + removed + " designer-only part(s) from the cloned card.");
        }

        private static void Wire(Button b, UnityEngine.Events.UnityAction action)
        {
            if (b == null) return;
            b.onClick.RemoveAllListeners();
            if (action != null) b.onClick.AddListener(action);
        }

        private static void SetButtonLabel(Button b, string text)
        {
            if (b == null) return;
            TextMeshProUGUI t = b.GetComponentInChildren<TextMeshProUGUI>(true);
            if (t != null) t.text = text;
        }

        private static void DropLocaliser(GameObject go)
        {
            foreach (UILocaliseLabel l in go.GetComponents<UILocaliseLabel>())
            {
                UnityEngine.Object.DestroyImmediate(l);
            }
        }

        /// <summary>Clone a live UI object without letting the copy's scripts run before we've stripped it.</summary>
        private static GameObject CloneInactive(GameObject src)
        {
            bool wasActive = src.activeSelf;
            src.SetActive(false);
            try
            {
                return (GameObject)UnityEngine.Object.Instantiate(src);
            }
            finally
            {
                src.SetActive(wasActive);
            }
        }

        private class RelSection
        {
            public GameObject root;
            public TextMeshProUGUI title;
            public TextMeshProUGUI with;
            public TextMeshProUGUI name;
            public TextMeshProUGUI length;
            public Slider bar;
            public Vector2 basePos;
            public UIStaffMechanicBonusIcon trustNode;
            public UIStaffMechanicBonusIcon syncNode;
            public TextMeshProUGUI trustLabel;
            public TextMeshProUGUI syncLabel;
        }

        private static readonly MechanicBonus sTrustMark = new MechanicBonus { bonusUnlockAt = 50 };
        private static readonly MechanicBonus sSyncMark = new MechanicBonus { bonusUnlockAt = 100 };

        /// <summary>A bar node from the mechanic widget, minus its mechanic-bonus tooltip, with a small caption under it.</summary>
        private static void PrepareNode(UIStaffMechanicBonusIcon node, string caption, out TextMeshProUGUI label, GameObject section)
        {
            label = null;
            if (node == null) return;
            foreach (UnityEngine.EventSystems.EventTrigger t in node.GetComponentsInChildren<UnityEngine.EventSystems.EventTrigger>(true)) UnityEngine.Object.DestroyImmediate(t);
            // 0.5.4: the node carries the mechanic's big bonus box ("Bonus1"/"Bonus2": line, box, Level label) as a child.
            // Two drivers don't leave room for it, so keep just the node on the bar and a caption.
            for (int c = 0; c < node.transform.childCount; c++)
            {
                Transform ch = node.transform.GetChild(c);
                if (ch.name.StartsWith("Bonus")) ch.gameObject.SetActive(false);
            }
            // Caption: a copy of the section's "Chemistry with" style text, small, under the node.
            TextMeshProUGUI style = null;
            foreach (TextMeshProUGUI t in section.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (t.gameObject.name == "subtext") { style = t; break; }
            }
            if (style == null) return;
            GameObject cap = (GameObject)UnityEngine.Object.Instantiate(style.gameObject);
            cap.name = "Caption";
            cap.transform.SetParent(node.transform, false);
            DropLocaliser(cap);
            label = cap.GetComponent<TextMeshProUGUI>();
            label.text = caption;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
            RectTransform rt = (RectTransform)cap.transform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(120f, 22f);
            rt.anchoredPosition = new Vector2(0f, -16f);
        }

        private static readonly List<RelSection> sSections = new List<RelSection>();

        /// <summary>
        /// "Relationship With Drivers": two copies of the mechanic card's relationship widget (same
        /// title, "Chemistry with", driver name, weeks pill and purple bar), one per driver, with the
        /// mechanic-only parts (bonus icons, swap button, tooltips) removed and the widget script gone.
        /// </summary>
        private static void BuildRelationshipSections(StaffScreen screen)
        {
            sSections.Clear();
            UIStaffScreenDriverWidget proto = null;
            try
            {
                if (screen.headMechanicFirstDriver != null) proto = screen.headMechanicFirstDriver.driverWidget;
            }
            catch (Exception)
            {
            }
            if (proto == null)
            {
                Main.Log("STAFF CARD: no mechanic relationship widget to copy; using text rows.");
                return;
            }
            for (int i = 0; i < 2; i++)
            {
                GameObject go = CloneInactive(proto.gameObject);
                go.name = "DirectorRelationship" + i;
                go.transform.SetParent(sCard.transform, false);
                UIStaffScreenDriverWidget w = go.GetComponent<UIStaffScreenDriverWidget>();
                RelSection sec = new RelSection { root = go, title = w.titleLabel, with = w.chemistryWithLabel, length = w.chemistryLengthLabel, bar = w.chemistryAmount };
                foreach (TextMeshProUGUI t in go.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    if (t.gameObject.name == "DriverName" && t.transform.parent != null && t.transform.parent.name.StartsWith("TextGrid")) sec.name = t;
                }
                HideIf(w.swapDriverButton);
                sec.trustNode = w.bonusOne;
                sec.syncNode = w.bonusTwo;
                PrepareNode(sec.trustNode, "Trust", out sec.trustLabel, go);
                PrepareNode(sec.syncNode, "In Sync", out sec.syncLabel, go);
                HideIf(w.bonusOneBigIcon);
                HideIf(w.bonusTwoBigIcon);
                HideGo(w.modifiersContainer);
                HideGo(w.positiveIconModifier);
                HideGo(w.negativeIconModifier);
                HideGo(w.driverDetails);
                HideGo(w.enduranceDrivers);
                HideIf(w.mechanicsHistoryDriverChemistry);
                if (w.noData != null) foreach (GameObject g in w.noData) HideGo(g);
                if (i > 0)
                {
                    HideIf(sec.title);
                    Transform div = go.transform.Find("Divide (2)");
                    if (div != null) div.gameObject.SetActive(false);
                }
                Strip(go);
                UnityEngine.Object.DestroyImmediate(w);
                foreach (TextMeshProUGUI t in new[] { sec.title, sec.with, sec.name, sec.length })
                {
                    if (t != null) DropLocaliser(t.gameObject);
                }
                if (sec.bar != null)
                {
                    sec.bar.interactable = false;
                    sec.bar.minValue = 0f;
                    sec.bar.maxValue = 1f;
                }
                sec.basePos = ((RectTransform)proto.transform).anchoredPosition;
                go.SetActive(true);
                sSections.Add(sec);
            }
            Main.Log("STAFF CARD: relationship sections built (" + (sSections.Count > 0 && sSections[0].name != null ? "driver-name label found" : "no driver-name label") + ").");
        }

        private static void HideIf(Component c)
        {
            if (c != null) c.gameObject.SetActive(false);
        }

        private static void HideGo(GameObject g)
        {
            if (g != null) g.SetActive(false);
        }

        private static void FillSections(Team team, StrategyDirector d)
        {
            for (int i = 0; i < sSections.Count; i++)
            {
                RelSection sec = sSections[i];
                Driver drv = team != null ? team.GetDriver(i) : null;
                sec.root.SetActive(d != null && drv != null);
                if (d == null || drv == null) continue;
                DirectorRelationship rel = d.GetRelationship(drv);
                if (sec.title != null) sec.title.text = "Relationship With Drivers";
                if (sec.with != null) sec.with.text = "Chemistry with";
                if (sec.name != null) sec.name.text = drv.name;
                string status = rel.InSync ? "In Sync" : rel.HasTrust ? "Trust" : (rel.amount < 50f ? "Trust at 50%" : "Building");
                if (sec.length != null) sec.length.text = string.Format("{0} {1} together · {2}", rel.racesTogether, rel.racesTogether == 1 ? "race" : "races", status);
                if (sec.bar != null) sec.bar.value = Mathf.Clamp01(rel.amount / 100f);
                try
                {
                    if (sec.trustNode != null) sec.trustNode.Setup(rel.HasTrust, sTrustMark);
                    if (sec.syncNode != null) sec.syncNode.Setup(rel.InSync, sSyncMark);
                    if (sec.trustLabel != null) sec.trustLabel.fontSize = Mathf.Max(11f, sec.with != null ? sec.with.fontSize * 0.8f : 12f);
                    if (sec.syncLabel != null) sec.syncLabel.fontSize = Mathf.Max(11f, sec.with != null ? sec.with.fontSize * 0.8f : 12f);
                }
                catch (Exception e)
                {
                    Main.Error("Relationship markers failed: " + e.Message);
                }
            }
        }

        private static void OnRacePlan()
        {
            try
            {
                Team team = DirectorService.PlayerTeam;
                StrategyDirector d = DirectorService.FindDirector(team);
                if (d == null) return;
                DateTime ev = default(DateTime);
                try { ev = team.championship.GetCurrentEventDetails().eventDate; } catch (Exception) { }
                StringBuilder sb = new StringBuilder();
                for (int c = 0; c < 2; c++)
                {
                    Driver drv = team.GetDriver(c);
                    if (drv == null) continue;
                    sb.AppendLine(string.Format("{0}: <b>{1}</b>", drv.name, StrategyDirector.PlanName(d.PlanForDriver(drv, c, ev))));
                }
                sb.AppendLine();
                sb.AppendLine("Change a car's plan in Ctrl+F10 → Strategy Director, or answer the pre-race briefing mail.");
                FeedbackPopup.Open(d.shortName + " — race plan", sb.ToString());
            }
            catch (Exception e)
            {
                Main.Error("Race plan popup failed: " + e);
            }
        }

        private static void BuildRows(StaffScreen screen)
        {
            sRows = new GameObject("DirectorRelationships", typeof(RectTransform));
            sRows.transform.SetParent(sCard.transform, false);
            RectTransform rt = (RectTransform)sRows.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);

            // Text: a clone of the card's "expires" label keeps the game's font and colour.
            GameObject txt = (GameObject)UnityEngine.Object.Instantiate(sWidget.expires.gameObject);
            txt.name = "Text";
            txt.transform.SetParent(sRows.transform, false);
            sRowsText = txt.GetComponent<TextMeshProUGUI>();
            sRowsText.alignment = TextAlignmentOptions.TopLeft;
            sRowsText.enableWordWrapping = false;
            RectTransform trt = (RectTransform)txt.transform;
            trt.anchorMin = new Vector2(0f, 1f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.pivot = new Vector2(0f, 1f);

            // Bars: clones of the mechanic card's chemistry slider, if there is one.
            sBars.Clear();
            Slider proto = null;
            try
            {
                if (screen.headMechanicFirstDriver != null && screen.headMechanicFirstDriver.driverWidget != null)
                {
                    proto = screen.headMechanicFirstDriver.driverWidget.chemistryAmount;
                }
            }
            catch (Exception)
            {
            }
            for (int i = 0; proto != null && i < 2; i++)
            {
                GameObject bar = (GameObject)UnityEngine.Object.Instantiate(proto.gameObject);
                bar.name = "Bar" + i;
                bar.transform.SetParent(sRows.transform, false);
                Slider s = bar.GetComponent<Slider>();
                s.interactable = false;
                s.minValue = 0f;
                s.maxValue = 1f;
                sBars.Add(s);
            }
        }

        private static void Place(StaffScreen screen, Layout l)
        {
            RectTransform src = (RectTransform)screen.headDesign.transform;
            RectTransform rt = (RectTransform)sCard.transform;
            rt.anchorMin = src.anchorMin;
            rt.anchorMax = src.anchorMax;
            float width = src.rect.width;
            float height = l.height;
            // Top-align with the Designer card, then size the clone down to the compact height.
            float srcTop = src.anchoredPosition.y + src.rect.height * (1f - src.pivot.y);
            rt.pivot = new Vector2(src.pivot.x, 1f);
            rt.sizeDelta = new Vector2(src.sizeDelta.x, height);
            rt.anchoredPosition = new Vector2(src.anchoredPosition.x + l.offsetX, srcTop + l.offsetY);
            rt.localScale = new Vector3(l.scale, l.scale, 1f);
            sShowBars = l.bars;
            sCard.transform.SetAsLastSibling();

            for (int i = 0; i < sSections.Count; i++)
            {
                RectTransform sr = (RectTransform)sSections[i].root.transform;
                sr.anchoredPosition = sSections[i].basePos + new Vector2(0f, l.sec1Y + i * l.sec2Y);
            }
            sRows.SetActive(sSections.Count == 0 && l.textRows);
            RectTransform rows = (RectTransform)sRows.transform;
            float rowsWidth = l.rowsWidth;
            rows.sizeDelta = new Vector2(rowsWidth, l.rowsHeight);
            rows.anchoredPosition = new Vector2(l.rowsX, l.rowsY);
            RectTransform trt = (RectTransform)sRowsText.transform;
            trt.sizeDelta = new Vector2(0f, l.rowsHeight);
            trt.anchoredPosition = Vector2.zero;
            if (l.fontSize > 0f) sRowsText.fontSize = l.fontSize;
            float line = sRowsText.fontSize * 1.25f;
            for (int i = 0; i < sBars.Count; i++)
            {
                RectTransform b = (RectTransform)sBars[i].transform;
                b.anchorMin = new Vector2(1f, 1f);
                b.anchorMax = new Vector2(1f, 1f);
                b.pivot = new Vector2(1f, 0.5f);
                b.anchoredPosition = new Vector2(-4f, -line * (i + 1.5f));
            }
        }

        public static void Refresh()
        {
            if (sCard == null || sWidget == null)
            {
                return;
            }
            Team team = DirectorService.PlayerTeam;
            StrategyDirector d = DirectorService.FindDirector(team);
            if (sTitle != null) sTitle.text = "Strategy Director";
            if (d != null)
            {
                try
                {
                    sWidget.Setup(d);
                }
                catch (Exception e)
                {
                    Main.Error("Staff card setup failed: " + e.Message);
                    sWidget.personName.text = d.name;
                }
                Show(sWidget.portrait, true);
                Show(sWidget.flag, true);
                Show(sWidget.ability, true);
                Show(sWidget.detailsButton, true);
                Show(sWidget.fireButton, true);
                Show(sWidget.compareButton, true);
                Show(sWidget.negotiateButton, true);
                Show(sWidget.findButton, false);
                FillRows(team, d);
                FillSections(team, d);
            }
            else
            {
                sWidget.personName.text = "No strategy director";
                sWidget.age.text = "-";
                sWidget.expires.text = "Click Find to get applicants";
                sWidget.wage.text = "-";
                sWidget.costToBreakContract.text = "-";
                Show(sWidget.portrait, false);
                Show(sWidget.flag, false);
                Show(sWidget.ability, false);
                Show(sWidget.detailsButton, false);
                Show(sWidget.fireButton, false);
                Show(sWidget.compareButton, false);
                Show(sWidget.negotiateButton, false);
                Show(sWidget.findButton, true);
                sRowsText.text = "";
                FillSections(team, null);
                foreach (Slider s in sBars) s.gameObject.SetActive(false);
            }
        }

        private static void Show(Component c, bool on)
        {
            if (c != null) GameUtility.SetActive(c.gameObject, on);
        }

        private static void FillRows(Team team, StrategyDirector d)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<b>Relationships</b>");
            for (int i = 0; i < 2; i++)
            {
                Driver drv = team.GetDriver(i);
                float amount = 0f;
                string status = "";
                if (drv != null)
                {
                    DirectorRelationship rel = d.GetRelationship(drv);
                    amount = rel.amount;
                    status = rel.InSync ? "IN SYNC" : rel.HasTrust ? "TRUST" : "building";
                    sb.Append(string.Format("\n{0}  {1:0}%  {2}", drv.lastName, rel.amount, status));
                }
                if (i < sBars.Count)
                {
                    sBars[i].gameObject.SetActive(drv != null && sShowBars);
                    sBars[i].value = Mathf.Clamp01(amount / 100f);
                }
            }
            sRowsText.text = sb.ToString();
        }

        // ------------------------------------------------------------------
        // Buttons
        // ------------------------------------------------------------------

        public static void OpenDetails()
        {
            StrategyDirector d = DirectorService.Current;
            if (d == null) return;
            if (Main.Settings.DetailsScreen)
            {
                try
                {
                    UIManager.instance.ChangeScreen("StaffDetailsScreen", d);
                    return;
                }
                catch (Exception e)
                {
                    Main.Error("Staff Details screen failed; showing the popup instead: " + e.Message);
                }
            }
            OnDetails();
        }

        public static void DetailsPopup() { OnDetails(); }

        private static void OnDetails()
        {
            try
            {
                Team team = DirectorService.PlayerTeam;
                StrategyDirector d = DirectorService.FindDirector(team);
                if (d == null) return;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(string.Format("{0} · {1} · age {2}", StrategyDirector.StarText(d.stats.GetAbility()), d.nationality != null ? d.nationality.localisedNationality : "", d.GetAge()));
                sb.AppendLine(string.Format("Planning {0:0}/20 (plan accuracy) · Reactions {1:0}/20 (calls on time) · Potential {2:0}/20", d.stats.planning, d.stats.reactions, d.stats.potential));
                sb.AppendLine(string.Format("Specialty: {0} — {1}.", StrategyDirector.SpecialtyName(d.specialty), StrategyDirector.SpecialtyBlurb(d.specialty)));
                sb.AppendLine(string.Format("Contract: {0}/year ({1}/race), ends {2:d MMM yyyy}. Races run: {3}.",
                    GameUtility.GetCurrencyString(d.contract.yearlyWages), GameUtility.GetCurrencyString(d.contract.perRaceCost), d.contract.endDate, d.racesRun));
                sb.AppendLine();
                for (int i = 0; i < 2; i++)
                {
                    Driver drv = team.GetDriver(i);
                    if (drv == null) continue;
                    DirectorRelationship rel = d.GetRelationship(drv);
                    string bonus = rel.InSync ? "IN SYNC: calls always on time" : rel.HasTrust ? "TRUST: +5% call accuracy" : "building (Trust at 50%, In Sync at 100%)";
                    sb.AppendLine(string.Format("{0}: {1:0}% over {2} race(s) — {3}.", drv.name, rel.amount, rel.racesTogether, bonus));
                }
                FeedbackPopup.Open(d.name + " — Strategy Director", sb.ToString());
            }
            catch (Exception e)
            {
                Main.Error("Director details failed: " + e);
            }
        }

        /// <summary>Called after a fire or renewal (the Staff Details screen sets this to refresh itself).</summary>
        public static Action AfterChange;

        private static void Changed()
        {
            Refresh();
            Action a = AfterChange;
            AfterChange = null;
            if (a != null)
            {
                try { a(); } catch (Exception e) { Main.Error("Refresh after change failed: " + e.Message); }
            }
        }

        public static void FireFlow() { OnFire(); }
        public static void RenewFlow() { OnRenew(); }
        public static void RacePlanFlow() { OnRacePlan(); }

        private static void OnFire()
        {
            try
            {
                Team team = DirectorService.PlayerTeam;
                StrategyDirector d = DirectorService.FindDirector(team);
                if (d == null) return;
                int cost = d.contract.GetContractTerminationCost();
                GenericConfirmation dialog = UIManager.instance.dialogBoxManager.GetDialog<GenericConfirmation>();
                string text = string.Format("Release {0} from their contract? Breaking it costs {1}. The cars go back to you until you hire a new director (Find on this card).",
                    d.name, GameUtility.GetCurrencyString(cost));
                dialog.Show(null, "Cancel", () => ConfirmFire(team, d, cost), "Fire", text, "Fire strategy director");
            }
            catch (Exception e)
            {
                Main.Error("Fire dialog failed: " + e);
            }
        }

        private static void ConfirmFire(Team team, StrategyDirector d, int cost)
        {
            Action ok = () =>
            {
                DirectorService.Fire(team, d, "fired by you from the Staff screen");
                Changed();
            };
            Action fail = () => FeedbackPopup.Open("Can't afford it", "There isn't enough budget to pay off " + d.name + "'s contract.");
            if (cost <= 0)
            {
                ok();
                return;
            }
            Transaction t = new Transaction(Transaction.Group.HQUpkeepAndStaff, Transaction.Type.Debit, cost, "Strategy director contract termination (" + d.shortName + ")");
            team.financeController.finance.ProcessTransactions(ok, fail, true, t);
        }

        private static void OnRenew()
        {
            try
            {
                Team team = DirectorService.PlayerTeam;
                StrategyDirector d = DirectorService.FindDirector(team);
                if (d == null) return;
                int ask = DirectorService.RenewalAsk(team, d);
                GenericConfirmation dialog = UIManager.instance.dialogBoxManager.GetDialog<GenericConfirmation>();
                string text = string.Format("{0}'s contract runs to {1:d MMM yyyy}. They'd extend for {2} a year (now {3}).",
                    d.name, d.contract.endDate, GameUtility.GetCurrencyString(ask), GameUtility.GetCurrencyString(d.contract.yearlyWages));
                dialog.Show(null, "Not now", () => { DirectorService.Renew(team, d, 1, ask); Changed(); }, "Renew 1 year", text, "Renew strategy director");
                dialog.SetIgnoreButton(() => { DirectorService.Renew(team, d, 2, ask); Changed(); }, "Renew 2 years");
            }
            catch (Exception e)
            {
                Main.Error("Renew dialog failed: " + e);
            }
        }

        private static void OnFind()
        {
            try
            {
                Team team = DirectorService.PlayerTeam;
                if (team == null) return;
                if (DirectorService.FindDirector(team) != null)
                {
                    FeedbackPopup.Open("Strategy director", "You already have a strategy director. Fire them first to look for a new one.");
                    return;
                }
                DirectorService.SendApplicantsMail(team);
                FeedbackPopup.Open("Strategy director applicants", "Three applicants have written in. Their details are in your inbox (Staff).");
            }
            catch (Exception e)
            {
                Main.Error("Find applicants failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // Layout dump (to tune the placement from outside the game)
        // ------------------------------------------------------------------

        private static void Dump(StaffScreen screen)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Staff screen dump " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  screen " + Screen.width + "x" + Screen.height);
                Canvas canvas = screen.GetComponentInParent<Canvas>();
                if (canvas != null) sb.AppendLine("Canvas scaleFactor " + canvas.scaleFactor);
                sb.AppendLine("== StaffScreen (depth 4)");
                DumpTree(sb, screen.transform, 0, 4);
                sb.AppendLine("== headDesign card (full)");
                DumpTree(sb, screen.headDesign.transform, 0, 8);
                if (screen.headMechanicFirstDriver != null)
                {
                    sb.AppendLine("== headMechanicFirstDriver card (depth 3)");
                    DumpTree(sb, screen.headMechanicFirstDriver.transform, 0, 3);
                }
                File.WriteAllText(Path.Combine(Main.Entry.Path, "staff-screen-dump.txt"), sb.ToString());
            }
            catch (Exception e)
            {
                Main.Error("Staff screen dump failed: " + e.Message);
            }
        }

        private static void DumpTree(StringBuilder sb, Transform t, int depth, int max)
        {
            RectTransform r = t as RectTransform;
            string comps = "";
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                comps += (comps.Length > 0 ? "," : "") + c.GetType().Name;
            }
            string text = "";
            TextMeshProUGUI tmp = t.GetComponent<TextMeshProUGUI>();
            if (tmp != null && tmp.text != null) text = " \"" + tmp.text.Replace("\n", " ").Substring(0, Math.Min(40, tmp.text.Length)) + "\"";
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}{1} [{2}] {3}{4}{5}",
                new string(' ', depth * 2), t.name, comps, t.gameObject.activeSelf ? "" : "(inactive) ",
                r == null ? "" : string.Format(CultureInfo.InvariantCulture, "pos({0:0},{1:0}) size({2:0}x{3:0}) rect({4:0}x{5:0}) aMin({6},{7}) aMax({8},{9}) pivot({10},{11})",
                    r.anchoredPosition.x, r.anchoredPosition.y, r.sizeDelta.x, r.sizeDelta.y, r.rect.width, r.rect.height, r.anchorMin.x, r.anchorMin.y, r.anchorMax.x, r.anchorMax.y, r.pivot.x, r.pivot.y),
                text));
            if (depth >= max) return;
            for (int i = 0; i < t.childCount; i++)
            {
                DumpTree(sb, t.GetChild(i), depth + 1, max);
            }
        }
    }

    [HarmonyPatch(typeof(StaffScreen), "OnEnter")]
    internal static class StaffScreenEnterPatch
    {
        private static void Postfix(StaffScreen __instance)
        {
            try
            {
                StaffCard.OnStaffScreen(__instance);
            }
            catch (Exception e)
            {
                Main.Error("Staff card failed: " + e);
            }
        }
    }
}
