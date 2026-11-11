using System;
using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>Interactables for objects (명세서 14.4, 16.7, 16.10): pick up / drop, alarm call point, faucet,
    /// towel, elevator button and extinguisher. Owns the Inventory and the objects' visible state.</summary>
    public sealed class ItemInteractions
    {
        public const string FireTarget = "fire";

        private readonly BuildingView _view;
        private readonly ZoneGraph _graph;
        private readonly Scenario _scenario;
        private readonly FireConfig _fireCfg;
        private readonly RuntimeConfig _rt;
        private readonly FireDirector _fire;
        private readonly IRunRecorder _recorder;
        private readonly IPlayerRig _rig;
        private readonly GameFlow _flow;
        private readonly Dictionary<string, BuildingObject> _objects = new Dictionary<string, BuildingObject>();
        private readonly Dictionary<string, string> _kinds = new Dictionary<string, string>();
        private float _busyUntil; // scaled game time
        private float _sprayed;      // seconds sprayed within range while the fire was initial (FireEngine.Extinguish)
        private float _extinguisherUsed;

        public Inventory Inventory { get; }
        public string LastRefusal { get; private set; }
        /// <summary>Picking up takes pickup_s: movement and other intents wait.</summary>
        public bool Busy => Time.time < _busyUntil;
        public float ExtinguisherLeftS => Mathf.Max(0, _rt.Items.ExtinguisherCapacityS - _extinguisherUsed);

        public event Action AlarmPressed;
        public event Action<bool> Sprayed; // success
        public event Action ElevatorCalled;

        public ItemInteractions(BuildingView view, ZoneGraph graph, Scenario scenario, FireConfig fireCfg, RuntimeConfig rt,
                                FireDirector fire, IRunRecorder recorder, IPlayerRig rig, GameFlow flow, IInteractionSource input)
        {
            _view = view;
            _graph = graph;
            _scenario = scenario;
            _fireCfg = fireCfg;
            _rt = rt;
            _fire = fire;
            _recorder = recorder;
            _rig = rig;
            _flow = flow;
            foreach (var z in graph.Zones)
                foreach (var o in z.Objects ?? new List<BuildingObject>())
                {
                    _objects[o.Id] = o;
                    _kinds[o.Id] = Snake(o.Kind);
                }
            Inventory = new Inventory(rt.Items.BagSlots, id => _kinds.TryGetValue(id, out var k) ? k : null);
            input.IntentRaised += Handle;
            flow.RunStarted += _ => ResetItems();
            flow.StateChanged += (from, to) =>
            {
                // Retry-style start (명세서 16.12): the errand document is already in hand.
                if (to == FlowState.PreFire && from != FlowState.Tutorial) GiveErrandObject();
            };
        }

        public string KindOf(string objectId) => _kinds.TryGetValue(objectId, out var k) ? k : null;

        public bool IsPickable(string objectId) =>
            _kinds.TryGetValue(objectId, out var k) && _rt.Scene.Objects.TryGetValue(k, out var s) && s.Pickable;

        private void ResetItems()
        {
            Inventory.Reset();
            _sprayed = _extinguisherUsed = 0;
            foreach (var o in _objects.Values)
            {
                if (!_view.Objects.TryGetValue(o.Id, out var v)) continue;
                v.Go.transform.position = SceneBuilder.ToWorld(o.Pos);
                v.Go.SetActive(true);
            }
        }

        private void GiveErrandObject()
        {
            var id = _scenario.Tutorial?.ErrandObject;
            if (id == null || !_objects.ContainsKey(id) || Inventory.Has(id)) return;
            Inventory.Pickup(id);
            SetVisible(id, false);
        }

        private void Handle(Intent intent)
        {
            if (Busy) { LastRefusal = "busy picking up"; return; }
            switch (intent.Kind)
            {
                case IntentKind.Grab: Grab(intent.TargetId); break;
                case IntentKind.Use: Use(intent); break;
                case IntentKind.Drop: Drop(); break;
                case IntentKind.TowelToggle: ToggleTowel(); break;
            }
        }

        private void Grab(string id)
        {
            if (id == null || !IsPickable(id)) { LastRefusal = "not pickable: " + id; return; }
            if (Inventory.Has(id) || !_view.Objects[id].Go.activeSelf) { LastRefusal = "already taken: " + id; return; }
            if (!InRange(id, _rt.Pc.InteractRangeM)) return;
            var dropped = Inventory.Pickup(id);
            SetVisible(id, false);
            if (dropped != null) PlaceAtFeet(dropped);
            _busyUntil = Time.time + _rt.Pc.PickupS;
            var kind = _kinds[id];
            _recorder.Record(EventTypes.Pickup, new { item = kind, @object = id, essential = _rt.Scene.Objects[kind].Essential });
        }

        private void Use(Intent intent)
        {
            if (intent.TargetId == FireTarget) { Spray(intent.HoldSeconds); return; }
            var id = intent.TargetId;
            if (id == null || !_kinds.TryGetValue(id, out var kind)) { LastRefusal = "nothing to use: " + id; return; }
            if (!InRange(id, _rt.Pc.InteractRangeM)) return;
            switch (kind)
            {
                case "alarm":
                    _recorder.Record(EventTypes.AlarmPressed, new { @object = id });
                    _fire.AlarmPressed();
                    AlarmPressed?.Invoke();
                    break;
                case "faucet":
                    if (!Inventory.HasTowel) { LastRefusal = "no towel to wet"; break; }
                    if (intent.HoldSeconds + 1e-4f < _rt.Pc.FaucetHoldS) { LastRefusal = "faucet hold too short"; break; }
                    Inventory.WetTowel();
                    _recorder.Record(EventTypes.TowelWet);
                    break;
                case "elevator_button":
                    _recorder.Record(EventTypes.ElevatorCall);
                    ElevatorCalled?.Invoke();
                    break;
                default:
                    if (IsPickable(id)) Grab(id);
                    else LastRefusal = "cannot use " + kind;
                    break;
            }
        }

        private void Drop()
        {
            bool towelWasOn = Inventory.TowelEquipped && Inventory.HandKind == "towel";
            var id = Inventory.Drop();
            if (id == null) { LastRefusal = "nothing in hand"; return; }
            PlaceAtFeet(id);
            if (towelWasOn) _recorder.Record(EventTypes.TowelEquip, new { on = false });
        }

        private void ToggleTowel()
        {
            var on = Inventory.ToggleTowel();
            if (on == null) { LastRefusal = "no towel"; return; }
            _recorder.Record(EventTypes.TowelEquip, new { on = on.Value });
        }

        /// <summary>Extinguisher in hand, aimed at the fire for <paramref name="seconds"/> (mouse button held).</summary>
        private void Spray(float seconds)
        {
            if (Inventory.HandKind != "extinguisher") { LastRefusal = "no extinguisher in hand"; return; }
            float duration = Mathf.Min(seconds, ExtinguisherLeftS);
            if (duration <= 0) { LastRefusal = "extinguisher empty"; return; }
            _extinguisherUsed += duration;
            var engine = _fire.Engine;
            var phase = engine.Phase;
            float distance = HorizontalDistance(FireCenter());
            if (phase == FirePhase.Initial && distance <= _fireCfg.ExtinguishRangeM) _sprayed += duration;
            var result = engine.Extinguish(_flow.T, _sprayed, distance);
            bool success = result == ExtinguishResult.Extinguished && phase != FirePhase.Extinguished;
            if (phase != FirePhase.None && phase != FirePhase.Extinguished)
                _recorder.Record(EventTypes.Extinguish, new { phase = phase.ToString().ToLowerInvariant(), duration = (float)Math.Round(duration, 2), success });
            Sprayed?.Invoke(success);
        }

        public Vector3 FireCenter()
        {
            var z = _graph.Zone(_scenario.Fire.OriginZone);
            return new Vector3(z.Polygon.Average(p => p.X), 0, z.Polygon.Average(p => p.Z));
        }

        private void SetVisible(string id, bool visible)
        {
            if (_view.Objects.TryGetValue(id, out var v)) v.Go.SetActive(visible);
        }

        private void PlaceAtFeet(string id)
        {
            if (!_view.Objects.TryGetValue(id, out var v)) return;
            var h = _rig.Head;
            var f = _rig.Forward;
            var flat = new Vector3(f.X, 0, f.Z);
            flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
            v.Go.transform.position = new Vector3(h.X, 0, h.Z) + flat * 0.4f + Vector3.up * (v.Go.transform.localScale.y / 2f);
            v.Go.SetActive(true);
        }

        private bool InRange(string id, float range)
        {
            var pos = _view.Objects.TryGetValue(id, out var v) ? v.Go.transform.position : SceneBuilder.ToWorld(_objects[id].Pos);
            float d = HorizontalDistance(pos);
            if (d <= range) return true;
            LastRefusal = $"{id} too far ({d:0.00} m > {range} m)";
            Debug.Log("[FireDrill] " + LastRefusal);
            return false;
        }

        private float HorizontalDistance(Vector3 p)
        {
            var h = _rig.Head;
            return Vector2.Distance(new Vector2(h.X, h.Z), new Vector2(p.x, p.z));
        }

        private static string Snake(ObjectKind k) =>
            string.Concat(k.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));
    }
}
