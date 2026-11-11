using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FireDrill.Runtime
{
    /// <summary>Run-time materials on URP shaders (kept in builds by ProjectSetup's always-included list).</summary>
    public static class Materials
    {
        public const string LitShader = "Universal Render Pipeline/Lit";
        public const string UnlitShader = "Universal Render Pipeline/Unlit";
        public const string ParticleShader = "Universal Render Pipeline/Particles/Unlit";

        private static readonly Dictionary<string, Material> Cache = new Dictionary<string, Material>();

        public static Color Parse(string hex)
        {
            if (!ColorUtility.TryParseHtmlString(hex, out var c)) throw new System.ArgumentException("bad color " + hex);
            return c;
        }

        private static Shader Find(string name) =>
            Shader.Find(name) ?? Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/InternalErrorShader");

        /// <summary>Alpha-blended, double-sided, no depth write; multiplies vertex colours (smoke, particles).</summary>
        public static Material Transparent(Color color, int queueOffset = 0, bool additive = false)
        {
            var m = new Material(Find(ParticleShader)) { name = "transparent" };
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", additive ? 2f : 0f);
            m.SetFloat("_Cull", (float)CullMode.Off);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_SrcBlend", (float)(additive ? BlendMode.SrcAlpha : BlendMode.SrcAlpha));
            m.SetFloat("_DstBlend", (float)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent + queueOffset;
            SetColor(m, color);
            return m;
        }

        /// <summary>Unlit opaque (signs, floor lines). Cached by colour.</summary>
        public static Material Unlit(string hex, int queue = -1)
        {
            var key = "u" + hex + queue;
            if (Cache.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(Find(UnlitShader)) { name = key };
            SetColor(m, Parse(hex));
            if (queue >= 0) m.renderQueue = queue;
            return Cache[key] = m;
        }

        private static Texture2D _softDot;

        /// <summary>Round particle sprite with soft edges (made in code; no texture assets yet).</summary>
        public static Texture2D SoftDot
        {
            get
            {
                if (_softDot != null) return _softDot;
                const int n = 64;
                _softDot = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "soft_dot", wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                        float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                        _softDot.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                    }
                _softDot.Apply();
                return _softDot;
            }
        }

        /// <summary>Transparent particle material with the soft round sprite.</summary>
        public static Material Particle(int queueOffset, bool additive)
        {
            var m = Transparent(Color.white, queueOffset, additive);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", SoftDot);
            m.mainTexture = SoftDot;
            return m;
        }

        public static void SetColor(Material m, Color c)
        {
            m.color = c;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        }
    }
}
