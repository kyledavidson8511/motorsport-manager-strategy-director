using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StrategyDirectorMod
{
    /// <summary>
    /// 0.5.2: the director's name as a tab in the Staff sub-menu along the bottom bar, next to
    /// the Lead Designer and the two mechanics. It's a copy of the Lead Designer's tab
    /// (NavigationSubButton, AssignHeadDesign) with the game's script removed, so it never
    /// tries to open the vanilla Staff Details screen with our person type; it opens the
    /// director's Details popup instead.
    /// </summary>
    public class DirectorTab : MonoBehaviour
    {
        private const string TabName = "StrategyDirectorTab";
        private static DirectorTab sInstance;

        private Toggle mToggle;
        private TextMeshProUGUI mLabel;
        private float mNextRefresh;

        public static void Ensure()
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShowStaffCard)
                {
                    if (sInstance != null) sInstance.gameObject.SetActive(false);
                    return;
                }
                if (sInstance == null)
                {
                    Create();
                }
                if (sInstance != null)
                {
                    sInstance.RefreshNow();
                }
            }
            catch (Exception e)
            {
                Main.Error("Director tab failed: " + e.Message);
            }
        }

        private static void Create()
        {
            // 0.5.3: pick the Lead Designer tab that belongs to the Staff button's own sub-menu.
            // (0.5.2 took the first AssignHeadDesign tab it found, which sat under the Team button.)
            NavigationSubButton src = null;
            string seen = "";
            foreach (NavigationButton nb in Resources.FindObjectsOfTypeAll<NavigationButton>())
            {
                if (nb == null || nb.subMenuContentHolder == null || nb.gameObject.hideFlags != HideFlags.None || !nb.gameObject.scene.IsValid()) continue;
                foreach (NavigationSubButton b in nb.subMenuContentHolder.GetComponentsInChildren<NavigationSubButton>(true))
                {
                    if (b.options != NavigationSubButton.SpecialOptions.AssignHeadDesign) continue;
                    seen += (seen.Length > 0 ? "; " : "") + nb.name + " → " + nb.goToScreen + " (holder " + nb.subMenuContentHolder.name + ")";
                    if (src == null && (nb.goToScreen == "StaffScreen" || nb.name.IndexOf("Staff", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        src = b;
                    }
                }
            }
            Main.Log("STAFF TAB: Lead Designer tabs found under: " + (seen.Length > 0 ? seen : "none") + ".");
            if (src == null)
            {
                Main.Log("STAFF TAB: no Lead Designer tab found in the bottom bar; skipping.");
                return;
            }
            Transform parent = src.transform;
            Transform existing = parent.Find(TabName);
            if (existing != null)
            {
                sInstance = existing.GetComponent<DirectorTab>();
                if (sInstance != null) return;
                UnityEngine.Object.Destroy(existing.gameObject);
            }

            GameObject go;
            bool wasActive = src.gameObject.activeSelf;
            src.gameObject.SetActive(false);
            try
            {
                go = (GameObject)UnityEngine.Object.Instantiate(src.gameObject);
            }
            finally
            {
                src.gameObject.SetActive(wasActive);
            }
            go.name = TabName;
            // 0.5.4: the sub-menu animates each tab into place by path, so a sibling copy stays at the
            // animation's start pose (faded, offset). As a child of the Lead Designer's tab it moves and
            // fades with it; it sits directly to that tab's left.
            go.transform.SetParent(src.transform, false);
            LayoutElement le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            RectTransform srcRt = (RectTransform)src.transform;
            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(srcRt.rect.width, srcRt.rect.height);
            rt.anchoredPosition = new Vector2(-srcRt.rect.width - 2f, 0f);
            rt.localScale = Vector3.one;
            AddBackground(go, src.transform.parent);
            // Remove any stray copy 0.5.2 left under the wrong button.
            foreach (DirectorTab old in Resources.FindObjectsOfTypeAll<DirectorTab>())
            {
                if (old != null && old.gameObject != go && old.gameObject.scene.IsValid()) UnityEngine.Object.Destroy(old.gameObject);
            }

            NavigationSubButton copy = go.GetComponent<NavigationSubButton>();
            TextMeshProUGUI label = copy != null ? copy.driverName : null;
            if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
            if (label == null) label = go.GetComponentInChildren<TextMeshProUGUI>(true);
            foreach (UILocaliseLabel l in go.GetComponentsInChildren<UILocaliseLabel>(true)) UnityEngine.Object.DestroyImmediate(l);
            foreach (UITooltipTrigger t in go.GetComponentsInChildren<UITooltipTrigger>(true)) UnityEngine.Object.DestroyImmediate(t);

            DirectorTab tab = go.AddComponent<DirectorTab>();
            tab.mLabel = label;
            tab.mToggle = go.GetComponentInChildren<Toggle>(true);
            if (tab.mToggle != null)
            {
                tab.mToggle.group = null;
                tab.mToggle.onValueChanged.RemoveAllListeners();
                tab.mToggle.isOn = false;
                tab.mToggle.onValueChanged.AddListener(tab.OnToggle);
            }
            else
            {
                Button b = go.GetComponentInChildren<Button>(true);
                if (b != null)
                {
                    b.onClick.RemoveAllListeners();
                    b.onClick.AddListener(StaffCard.OpenDetails);
                }
            }
            sInstance = tab;
            go.SetActive(true);
            Main.Log("STAFF TAB: added the director's tab to the Staff sub-menu.");
        }

        /// <summary>
        /// 1.0.0: the dark backing behind the staff tabs belongs to the bar that holds them, not to
        /// each tab, so the copy (sitting left of that bar) needs its own: same sprite and colour.
        /// </summary>
        private static void AddBackground(GameObject tab, Transform holder)
        {
            Image src = null;
            for (Transform t = holder; t != null && src == null; t = t.parent)
            {
                Image own = t.GetComponent<Image>();
                if (own != null && own.enabled && own.color.a > 0.2f) { src = own; break; }
                for (int i = 0; i < t.childCount && src == null; i++)
                {
                    Transform c = t.GetChild(i);
                    if (c.GetComponentInChildren<NavigationSubButton>(true) != null || c.GetComponent<DirectorTab>() != null) continue;
                    Image img = c.GetComponent<Image>();
                    if (img != null && img.enabled && img.color.a > 0.2f) src = img;
                }
                if (t.GetComponent<Canvas>() != null) break;
            }
            GameObject bg = new GameObject("TabBackground", typeof(RectTransform));
            bg.transform.SetParent(tab.transform, false);
            bg.transform.SetAsFirstSibling();
            RectTransform rt = (RectTransform)bg.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            Image im = bg.AddComponent<Image>();
            im.raycastTarget = false;
            if (src != null)
            {
                im.sprite = src.sprite;
                im.type = src.type == Image.Type.Simple ? Image.Type.Simple : Image.Type.Sliced;
                im.color = src.color;
                Main.Log("STAFF TAB: background copied from '" + src.gameObject.name + "'.");
            }
            else
            {
                im.color = new Color(0.12f, 0.13f, 0.16f, 0.95f);
                Main.Log("STAFF TAB: no bar background found; using a plain dark backing.");
            }
        }

        private void OnToggle(bool on)
        {
            if (!on)
            {
                return;
            }
            StaffCard.OpenDetails();
        }

        private void Update()
        {
            if (Time.unscaledTime < mNextRefresh)
            {
                return;
            }
            mNextRefresh = Time.unscaledTime + 0.25f;
            RefreshNow();
        }

        public void RefreshNow()
        {
            try
            {
                StrategyDirector d = Game.IsActive() ? DirectorService.Current : null;
                bool show = d != null && Main.Enabled && Main.Settings != null && Main.Settings.ShowStaffCard;
                if (gameObject.activeSelf != show)
                {
                    gameObject.SetActive(show);
                }
                if (show && mLabel != null && mLabel.text != d.name)
                {
                    mLabel.text = d.name;
                }
                // Highlighted while her Details screen is showing, like the other staff tabs.
                if (show && mToggle != null)
                {
                    bool here = false;
                    try
                    {
                        if (UIManager.instance.currentScreen_name == "StaffDetailsScreen" || UIManager.instance.currentScreen_screenName == "StaffDetailsScreen")
                        {
                            StaffDetailsScreen sd = UIManager.instance.GetScreen<StaffDetailsScreen>();
                            here = sd != null && sd.person == d;
                        }
                    }
                    catch (Exception)
                    {
                    }
                    if (mToggle.isOn != here)
                    {
                        mToggle.onValueChanged.RemoveListener(OnToggle);
                        mToggle.isOn = here;
                        mToggle.onValueChanged.AddListener(OnToggle);
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
