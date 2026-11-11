using System.Collections.Generic;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>Interactables for doors (명세서 14.4): open/close (F), handle check (H), emergency stop (Esc).
    /// Owns door state; relays it to the fire engine and logs DOOR_OPEN / DOOR_CLOSE / HANDLE_CHECK.
    /// checked_before = a HANDLE_CHECK of the same door within evaluation door_check.check_window_s.</summary>
    public sealed class DoorInteractions
    {
        private readonly BuildingView _view;
        private readonly ZoneGraph _graph;
        private readonly Scenario _scenario;
        private readonly FireDirector _fire;
        private readonly IRunRecorder _recorder;
        private readonly IPlayerRig _rig;
        private readonly GameFlow _flow;
        private readonly RuntimeConfig.PcControls _pc;
        private readonly Dictionary<string, Edge> _doors = new Dictionary<string, Edge>();
        private readonly Dictionary<string, float> _checkedAt = new Dictionary<string, float>();
        private readonly float _checkWindowS;

        /// <summary>Last intent that was refused and why (debug overlay, tests).</summary>
        public string LastRefusal { get; private set; }
        /// <summary>Handle checked (edge, hot): feedback for BodyEffects and sound.</summary>
        public event System.Action<string, bool> HandleChecked;

        public DoorInteractions(BuildingView view, ZoneGraph graph, Scenario scenario, FireDirector fire, IRunRecorder recorder,
                                IPlayerRig rig, GameFlow flow, RuntimeConfig.PcControls pc, IInteractionSource input,
                                EvaluationConfig evaluation)
        {
            _view = view;
            _graph = graph;
            _scenario = scenario;
            _fire = fire;
            _recorder = recorder;
            _rig = rig;
            _flow = flow;
            _pc = pc;
            var doorCheck = evaluation.Items.Find(i => i.Id == "door_check");
            _checkWindowS = doorCheck != null && doorCheck.Thresholds.TryGetValue("check_window_s", out var w) ? w : float.MaxValue;
            fire.DoorEvent += (edge, open) => SetDoor(edge, open, instant: false, relay: false);
            foreach (var e in graph.Edges)
                if (e.Type == EdgeType.Door) _doors[e.Id] = e;
            input.IntentRaised += Handle;
            flow.RunStarted += _ => ResetDoors();
            ResetDoors();
        }

        public bool IsOpen(string edgeId) => _view.Doors.TryGetValue(edgeId, out var d) && d.IsOpen;

        /// <summary>Someone other than the trainee (an NPC) opens or closes a door: no DOOR_OPEN/CLOSE log (명세서 16.9).</summary>
        public void SetBySystem(string edgeId, bool open)
        {
            if (!_doors.ContainsKey(edgeId)) return;
            Debug.Log($"[FireDrill] door {edgeId} {(open ? "opened" : "closed")} by an NPC at t={_flow.T:0.0}");
            SetDoor(edgeId, open, instant: false);
        }

        public bool IsDoor(string edgeId) => edgeId != null && _doors.ContainsKey(edgeId);

        private void ResetDoors()
        {
            _checkedAt.Clear();
            foreach (var e in _doors.Values)
            {
                bool open = e.DoorOpen ?? false;
                if (_scenario.InitialDoors != null && _scenario.InitialDoors.TryGetValue(e.Id, out var o)) open = o;
                SetDoor(e.Id, open, instant: true);
            }
        }

        private void Handle(Intent intent)
        {
            switch (intent.Kind)
            {
                case IntentKind.Abort:
                    _flow.Abort("staff");
                    break;
                case IntentKind.ToggleDoor:
                    if (Door(intent.TargetId, out var e) && InRange(new Vec3(e.Center.X, 0, e.Center.Z), _pc.InteractRangeM, "door"))
                        Toggle(e);
                    break;
                case IntentKind.HandleCheck:
                    if (!Door(intent.TargetId, out var d)) break;
                    if (intent.HoldSeconds + 1e-4f < _pc.HandleHoldS) { LastRefusal = "handle hold too short"; break; }
                    var handle = d.Handle ?? new Vec3(d.Center.X, 1f, d.Center.Z);
                    if (!InRange(handle, _pc.HandleRangeM, "handle")) break;
                    _checkedAt[d.Id] = _flow.T;
                    bool hot = Hot(d);
                    _recorder.Record(EventTypes.HandleCheck, new { edge = d.Id, hot });
                    HandleChecked?.Invoke(d.Id, hot);
                    break;
            }
        }

        private void Toggle(Edge e)
        {
            bool open = !IsOpen(e.Id);
            bool checkedBefore = _checkedAt.TryGetValue(e.Id, out var at) && _flow.T - at <= _checkWindowS + 1e-4f;
            var data = new { edge = e.Id, hot = Hot(e), checked_before = checkedBefore };
            SetDoor(e.Id, open, instant: false);
            _recorder.Record(open ? EventTypes.DoorOpen : EventTypes.DoorClose, data);
        }

        private void SetDoor(string edgeId, bool open, bool instant, bool relay = true)
        {
            if (_view.Doors.TryGetValue(edgeId, out var door)) door.SetOpen(open, instant);
            if (relay) _fire.SetDoor(edgeId, open);
        }

        /// <summary>Hot as felt from the trainee's side.</summary>
        private bool Hot(Edge e)
        {
            var zone = _graph.ZoneAt(_rig.Head);
            var from = zone == e.From || zone == e.To ? zone : e.From;
            return _fire.Engine.IsDoorHot(e.Id, from);
        }

        private bool Door(string id, out Edge e)
        {
            if (id != null && _doors.TryGetValue(id, out e)) return true;
            e = null;
            LastRefusal = "not a door: " + id;
            return false;
        }

        /// <summary>Horizontal distance from the trainee to <paramref name="target"/>.</summary>
        private bool InRange(Vec3 target, float range, string what)
        {
            var h = _rig.Head;
            float d = Vec2.Distance(new Vec2(h.X, h.Z), new Vec2(target.X, target.Z));
            if (d <= range) return true;
            LastRefusal = $"{what} too far ({d:0.00} m > {range} m)";
            Debug.Log("[FireDrill] " + LastRefusal);
            return false;
        }
    }
}
