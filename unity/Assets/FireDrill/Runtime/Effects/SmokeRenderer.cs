using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>16.4 smoke: per zone a translucent prism from the smoke layer to the ceiling with a soft lower edge,
    /// screen fog while the head is inside the layer, and floor edge lines that stay visible. Presentation only.</summary>
    public sealed class SmokeRenderer : MonoBehaviour
    {
        private sealed class ZoneSmoke
        {
            public Zone Zone;
            public Mesh Mesh;
            public MeshRenderer Renderer;
            public Material Material;
            public float D, Layer;            // shown values
            public float FromD, FromLayer;    // at the last target
            public float ToD, ToLayer;        // engine values
            public float Progress = 1f;
        }

        private readonly List<ZoneSmoke> _zones = new List<ZoneSmoke>();
        private RuntimeConfig.EffectSettings _fx;
        private float _maxOpacity;
        private IFireEngine _engine;
        private IPlayerRig _rig;
        private ZoneGraph _graph;
        private Color _smokeColor, _fogColor;
        private bool _enabled = true;

        /// <summary>16.4 opacity = base + per_d × D, never above fire.render.max_opacity (10장 safety cap 0.7). 0 when no smoke.</summary>
        public static float Opacity(float d, RuntimeConfig.SmokeSettings s, float maxOpacity) =>
            d <= 1e-4f ? 0f : Mathf.Min(maxOpacity, s.BaseOpacity + s.PerDOpacity * d);

        public static float FogDensity(float c, RuntimeConfig.FogSettings f) => f.BaseDensity + f.PerCDensity * c;

        public IEnumerable<Material> SmokeMaterials => _zones.Select(z => z.Material);

        public void Init(ZoneGraph graph, IFireEngine engine, IPlayerRig rig, FireConfig fire, RuntimeConfig rt)
        {
            _graph = graph;
            _engine = engine;
            _rig = rig;
            _fx = rt.Effects;
            _maxOpacity = fire.Render.MaxOpacity;
            _smokeColor = Materials.Parse(_fx.Smoke.Color);
            _fogColor = Materials.Parse(_fx.Fog.Color);
            foreach (var z in graph.Zones)
            {
                var go = new GameObject("Smoke_" + z.Id);
                go.transform.SetParent(transform, false);
                var mesh = new Mesh { name = "smoke_" + z.Id };
                mesh.MarkDynamic();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                var mat = Materials.Transparent(new Color(_smokeColor.r, _smokeColor.g, _smokeColor.b, 0f));
                r.sharedMaterial = mat;
                var zs = new ZoneSmoke { Zone = z, Mesh = mesh, Renderer = r, Material = mat, Layer = z.CeilingH, FromLayer = z.CeilingH, ToLayer = z.CeilingH };
                _zones.Add(zs);
                Apply(zs);
                BuildFloorLines(z, rt);
            }
        }

        /// <summary>Paused or aborted: smoke and fog off (명세서 5장 Aborted, 10장 watchdog).</summary>
        public void SetVisible(bool visible)
        {
            _enabled = visible;
            foreach (var z in _zones) z.Renderer.enabled = visible && z.D > 1e-4f;
            if (!visible) RenderSettings.fog = false;
        }

        /// <summary>New engine values arrived (each fire tick): interpolate to them over smoke.lerp_s.</summary>
        public void OnTick()
        {
            foreach (var z in _zones)
            {
                var s = _engine.Get(z.Zone.Id);
                if (Mathf.Approximately(s.D, z.ToD) && Mathf.Approximately(s.SmokeLayerY, z.ToLayer)) continue;
                z.FromD = z.D; z.FromLayer = z.Layer;
                z.ToD = s.D; z.ToLayer = s.SmokeLayerY;
                z.Progress = 0f;
            }
        }

        /// <summary>Snap to the engine values (run reset, tests).</summary>
        public void Snap()
        {
            foreach (var z in _zones)
            {
                var s = _engine.Get(z.Zone.Id);
                z.D = z.FromD = z.ToD = s.D;
                z.Layer = z.FromLayer = z.ToLayer = s.SmokeLayerY > 0 ? s.SmokeLayerY : z.Zone.CeilingH;
                z.Progress = 1f;
                Apply(z);
            }
        }

        private void Update()
        {
            float step = _fx.Smoke.LerpS > 0 ? Time.deltaTime / _fx.Smoke.LerpS : 1f;
            foreach (var z in _zones)
            {
                if (z.Progress >= 1f) continue;
                z.Progress = Mathf.Min(1f, z.Progress + step);
                z.D = Mathf.Lerp(z.FromD, z.ToD, z.Progress);
                z.Layer = Mathf.Lerp(z.FromLayer, z.ToLayer, z.Progress);
                Apply(z);
            }
            UpdateFog();
        }

        private void UpdateFog()
        {
            if (!_enabled || _rig == null) return;
            var head = _rig.Head;
            var zone = _graph.ZoneAt(head);
            var zs = zone != null ? _zones.FirstOrDefault(z => z.Zone.Id == zone) : null;
            bool inside = zs != null && zs.D > 1e-4f && head.Y > zs.Layer;
            RenderSettings.fog = inside;
            if (!inside) return;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = _fogColor;
            RenderSettings.fogDensity = FogDensity(_engine.HeadConcentration(head), _fx.Fog);
        }

        private void Apply(ZoneSmoke z)
        {
            float a = Opacity(z.D, _fx.Smoke, _maxOpacity);
            z.Renderer.enabled = _enabled && a > 0f;
            if (a <= 0f) return;
            var c = _smokeColor;
            c.a = a;
            Materials.SetColor(z.Material, c);
            BuildPrism(z.Mesh, z.Zone, Mathf.Clamp(z.Layer, 0f, z.Zone.CeilingH), _fx.Smoke.EdgeFadeM);
        }

        /// <summary>Sides from (layer - fade, alpha 0) to the ceiling (alpha 1), a bottom face at the layer and a top
        /// face under the ceiling. Double-sided material, so faces show from inside and outside.</summary>
        private static void BuildPrism(Mesh mesh, Zone zone, float layer, float fade)
        {
            var poly = zone.Polygon;
            int n = poly.Count;
            float top = zone.CeilingH - 0.01f;
            float low = Mathf.Max(0f, layer - fade);
            var v = new List<Vector3>();
            var col = new List<Color>();
            var tri = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % n];
                int k = v.Count;
                v.Add(new Vector3(a.X, low, a.Z)); col.Add(new Color(1, 1, 1, 0));
                v.Add(new Vector3(b.X, low, b.Z)); col.Add(new Color(1, 1, 1, 0));
                v.Add(new Vector3(a.X, layer, a.Z)); col.Add(Color.white);
                v.Add(new Vector3(b.X, layer, b.Z)); col.Add(Color.white);
                v.Add(new Vector3(a.X, top, a.Z)); col.Add(Color.white);
                v.Add(new Vector3(b.X, top, b.Z)); col.Add(Color.white);
                tri.AddRange(new[] { k, k + 2, k + 1, k + 1, k + 2, k + 3 });
                tri.AddRange(new[] { k + 2, k + 4, k + 3, k + 3, k + 4, k + 5 });
            }
            var flat = poly.Select(p => new Vector2(p.X, p.Z)).ToList();
            var caps = SceneBuilder.Triangulate(flat);
            foreach (float y in new[] { layer, top })
            {
                int k = v.Count;
                foreach (var p in poly) { v.Add(new Vector3(p.X, y, p.Z)); col.Add(Color.white); }
                tri.AddRange(caps.Select(i => k + i));
            }
            mesh.Clear();
            mesh.SetVertices(v);
            mesh.SetColors(col);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
        }

        private void BuildFloorLines(Zone z, RuntimeConfig rt)
        {
            var line = rt.Effects.FloorLine;
            var mat = Materials.Unlit(line.Color, (int)UnityEngine.Rendering.RenderQueue.Transparent + 100);
            var poly = z.Polygon;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = new Vector3(poly[i].X, line.HeightM, poly[i].Z);
                var b = new Vector3(poly[(i + 1) % poly.Count].X, line.HeightM, poly[(i + 1) % poly.Count].Z);
                var strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
                strip.name = "FloorLine_" + z.Id;
                strip.transform.SetParent(transform, false);
                strip.transform.position = (a + b) / 2f;
                strip.transform.rotation = Quaternion.FromToRotation(Vector3.right, (b - a).normalized);
                strip.transform.localScale = new Vector3((b - a).magnitude, 0.005f, line.WidthM);
                strip.GetComponent<Renderer>().sharedMaterial = mat;
                Destroy(strip.GetComponent<Collider>());
            }
        }
    }
}
