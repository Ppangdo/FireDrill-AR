using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>16.5 fire: flame and black smoke particles plus a flickering orange light at the centre of the origin
    /// zone. Size follows the fire phase (initial, growing, full = the whole room). After extinguishing it shrinks
    /// over extinguish_shrink_s and leaves white smoke. Has a trigger collider (target "fire") to aim the extinguisher.</summary>
    public sealed class FireVfx : MonoBehaviour
    {
        private RuntimeConfig.FireLook _look;
        private IFireEngine _engine;
        private Zone _zone;
        private ParticleSystem _flames, _smoke;
        private Light _light;
        private SphereCollider _target;
        private float _scale;          // 0..1 multiplier while shrinking
        private Vector2 _size;         // current diameter, height
        private bool _enabled = true;

        public FirePhase ShownPhase { get; private set; } = FirePhase.None;
        public Vector2 Size => _size * _scale;

        public void Init(ZoneGraph graph, string originZone, IFireEngine engine, RuntimeConfig rt)
        {
            _look = rt.Effects.Fire;
            _engine = engine;
            _zone = graph.Zone(originZone);
            transform.position = new Vector3(_zone.Polygon.Average(p => p.X), 0f, _zone.Polygon.Average(p => p.Z));

            _flames = MakeSystem("Flames", Materials.Parse(_look.FlameColor), additive: true, lifetime: 0.7f);
            _smoke = MakeSystem("Smoke", Materials.Parse(_look.SmokeColor), additive: false, lifetime: 2.0f);

            var lightGo = new GameObject("FireLight");
            lightGo.transform.SetParent(transform, false);
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = Materials.Parse(_look.FlameColor);
            _light.range = _look.LightRangeM;

            _target = gameObject.AddComponent<SphereCollider>();
            _target.isTrigger = true;
            gameObject.AddComponent<InteractTarget>().TargetId = ItemInteractions.FireTarget;
            ResetFire();
        }

        public void ResetFire()
        {
            ShownPhase = FirePhase.None;
            _scale = 1f;
            _size = Vector2.zero;
            _flames.Clear();
            _smoke.Clear();
            SetSmokeColor(_look.SmokeColor);
            Show(false);
        }

        public void SetVisible(bool visible)
        {
            _enabled = visible;
            Show(visible && ShownPhase != FirePhase.None && _scale > 0f);
        }

        private void Show(bool on)
        {
            var fe = _flames.emission; fe.enabled = on && ShownPhase != FirePhase.Extinguished;
            var se = _smoke.emission; se.enabled = on;
            _light.enabled = on && ShownPhase != FirePhase.Extinguished;
            _target.enabled = on;
            if (!on) { _flames.Clear(); }
        }

        private void Update()
        {
            var phase = _engine.Phase;
            if (phase != ShownPhase)
            {
                if (phase == FirePhase.Extinguished) SetSmokeColor(_look.WhiteSmokeColor);
                ShownPhase = phase;
                Show(_enabled && phase != FirePhase.None);
            }
            if (phase == FirePhase.None || !_enabled) return;

            if (phase == FirePhase.Extinguished)
            {
                _scale = Mathf.MoveTowards(_scale, 0f, Time.deltaTime / Mathf.Max(_look.ExtinguishShrinkS, 1e-3f));
            }
            else _size = TargetSize(phase);

            var size = Size;
            var shape = _flames.shape; shape.radius = Mathf.Max(0.05f, size.x / 2f);
            var main = _flames.main; main.startSpeed = size.y / main.startLifetime.constant; main.startSize = Mathf.Max(0.15f, size.x * 0.5f);
            var emit = _flames.emission; emit.rateOverTime = 40f * Mathf.Max(0.2f, size.x);
            var sShape = _smoke.shape; sShape.radius = Mathf.Max(0.05f, size.x / 2f);
            var sMain = _smoke.main; sMain.startSpeed = (_zone.CeilingH) / sMain.startLifetime.constant; sMain.startSize = Mathf.Max(0.3f, size.x);
            _target.radius = Mathf.Max(0.3f, size.x / 2f);
            _target.center = new Vector3(0, Mathf.Max(0.3f, size.y / 2f), 0);

            float flicker = 1f + (Mathf.PerlinNoise(Time.time * 8f, 0.37f) - 0.5f) * 2f * _look.Flicker;
            _light.intensity = _look.LightIntensity * flicker * _scale;
            _light.transform.localPosition = new Vector3(0, Mathf.Max(0.3f, size.y * 0.6f), 0);
            if (phase == FirePhase.Extinguished && _scale <= 0f) { var fe = _flames.emission; fe.enabled = false; _light.enabled = false; }
        }

        private Vector2 TargetSize(FirePhase phase)
        {
            switch (phase)
            {
                case FirePhase.Initial: return new Vector2(_look.Initial.DiameterM, _look.Initial.HeightM);
                case FirePhase.Growing: return new Vector2(_look.Growing.DiameterM, _look.Growing.HeightM);
                case FirePhase.Full:
                    float w = _zone.Polygon.Max(p => p.X) - _zone.Polygon.Min(p => p.X);
                    float d = _zone.Polygon.Max(p => p.Z) - _zone.Polygon.Min(p => p.Z);
                    return new Vector2(Mathf.Min(w, d), _zone.CeilingH);
                default: return _size;
            }
        }

        private ParticleSystem MakeSystem(string name, Color color, bool additive, float lifetime)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // emit upwards
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.startLifetime = lifetime;
            main.startColor = color;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 500;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(additive ? 0.55f : 0.6f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = Materials.Particle(additive ? 10 : 5, additive);
            ps.Play();
            return ps;
        }

        private void SetSmokeColor(string hex)
        {
            var main = _smoke.main;
            main.startColor = Materials.Parse(hex);
        }
    }
}
