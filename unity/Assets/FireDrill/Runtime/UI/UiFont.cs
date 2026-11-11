using System.IO;
using FireDrill.Core;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;

namespace FireDrill.Runtime
{
    /// <summary>16.11 font: a dynamic font asset made at run time from Fonts/{ui.font_file}. When the file is missing
    /// the OS font ui.fallback_os_font is used with a warning (사람 결정 2026-11-11, instead of 16.1's error screen).</summary>
    public static class UiFont
    {
        private static FontAsset _cached;

        /// <summary>Where the font came from: the file path, "os:{name}", or null when nothing loaded.</summary>
        public static string Source { get; private set; }

        public static string FontPath(RuntimeConfig.UiSettings ui) =>
            Application.isEditor
                ? Path.Combine(Application.dataPath, "FireDrill", "Fonts", ui.FontFile)
                : Path.Combine(Application.streamingAssetsPath, "FireDrill", "Fonts", ui.FontFile);

        public static FontAsset Load(RuntimeConfig.UiSettings ui)
        {
            if (_cached != null) return _cached;
            var path = FontPath(ui);
            if (File.Exists(path))
            {
                _cached = FontAsset.CreateFontAsset(path, 0, ui.FontSamplingPt, ui.FontSamplingPt / 10, GlyphRenderMode.SDFAA,
                                                    ui.FontAtlasPx, ui.FontAtlasPx);
                if (_cached != null) { Source = path; return _cached; }
                Debug.LogWarning("[FireDrill] could not load font " + path);
            }
            else Debug.LogWarning($"[FireDrill] {path} is missing: using the OS font '{ui.FallbackOsFont}' (add the file for the demo)");
            _cached = FontAsset.CreateFontAsset(ui.FallbackOsFont, "Regular", ui.FontSamplingPt);
            Source = _cached != null ? "os:" + ui.FallbackOsFont : null;
            if (_cached == null) Debug.LogError("[FireDrill] no Korean font available; text will not render");
            return _cached;
        }
    }
}
