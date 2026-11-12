using FireDrill.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireDrill.Runtime
{
    /// <summary>Creates a UIDocument from Resources/FireDrillUI/{uxml}.uxml + {uss}.uss with run-time PanelSettings
    /// (reference resolution from runtime.json) and the Korean font (UiFont). Shared by UiRoot and ReportView.</summary>
    public static class UiPanel
    {
        public const string ResourceDir = "FireDrillUI/";

        public static UIDocument Create(GameObject host, RuntimeConfig.UiSettings ui, string uxml, int sortingOrder,
                                        out PanelSettings panel)
        {
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>(ResourceDir + "FireDrillTheme");
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(ui.ReferenceResolution[0], ui.ReferenceResolution[1]);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            panel.sortingOrder = sortingOrder;

            host.SetActive(false); // UIDocument builds its tree on enable: set everything first
            var doc = host.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>(ResourceDir + uxml);
            host.SetActive(true);

            var font = UiFont.Load(ui);
            if (font != null) doc.rootVisualElement.style.unityFontDefinition = FontDefinition.FromSDFFont(font);
            return doc;
        }

        public static void Show(VisualElement e, bool on)
        {
            if (e != null) e.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public static bool Shown(VisualElement e) => e != null && e.style.display.value != DisplayStyle.None;
    }
}
