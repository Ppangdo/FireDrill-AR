using System.Collections.Generic;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>Stage 3 part of Interactables (명세서 14.4): door open/close (F), handle check (H), emergency stop (Esc).
    /// Owns door state; relays it to the fire engine and logs DOOR_OPEN / DOOR_CLOSE / HANDLE_CHECK.
    /// Items, alarm, faucet, elevator and extinguisher come with stage 4.</summary>
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
        private readonly HashSet<string> _checked = new HashSet<string>();

        /// <summary>Last intent that was refused and why (debug overlay, tests).</summary>
        public string LastRefusal { get; private set; }

        public DoorInteractions(BuildingView view, ZoneGraph graph, Scenario scenario, FireDirector fire, IRunRecorder recorder,
                                IPlayerRig rig, GameFlow flow, RuntimeConfig.PcControls pc, IInteractionSource input)
        {
            _view = view;
            _graph = graph;
            _scenario = scenario;
            _fire = fire;
            _recorder = recorder;
            _rig = rig;
            _flow = flow;
            _pc = pc;
            foreach (var e in graph.Edges)
                if (e.Type == EdgeType.Door) _doors[e.Id] = e;
            input.IntentRaised += Handle;
            flow.RunStarted += _ => ResetDoors();
            ResetDoors();
        }

        public bool IsOpen(string edgeId) => _view.Doors.TryGetValue(edgeId, out var d) && d.IsOpen;

        private void ResetDoors()
        {
            _checked.Clear();
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
                    _checked.Add(d.Id);
                    _recorder.Record(EventTypes.HandleCheck, new { edge = d.Id, hot = Hot(d) });
                    break;
            }
        }

        private void Toggle(Edge e)
        {
            bool open = !IsOpen(e.Id);
            var data = new { edge = e.Id, hot = Hot(e), checked_before = _checked.Contains(e.Id) };
            SetDoor(e.Id, open, instant: false);
            _recorder.Record(open ? EventTypes.DoorOpen : EventTypes.DoorClose, data);
        }

        private void SetDoor(string edgeId, bool open, bool instant)
        {
            if (_view.Doors.TryGetValue(edgeId, out var door)) door.SetOpen(open, instant);
            _fire.SetDoor(edgeId, open);
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
