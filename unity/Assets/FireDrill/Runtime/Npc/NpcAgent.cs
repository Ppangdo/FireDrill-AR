using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    public enum NpcResult { Remained, Rescued, Collapsed }

    /// <summary>One NPC (명세서 16.9): a capsule with a name tag that walks straight between door centres from
    /// ZoneGraph.PathTo (no navmesh), opens closed doors on its way (pause, then leaves them open) and acts by its
    /// current NpcAction. Takes smoke like the trainee (head 1.6 m, 0.9 m low, no towel) and collapses at FED 1.0.</summary>
    public sealed class NpcAgent : MonoBehaviour
    {
        private Scenario.NpcSetup _setup;
        private RuntimeConfig.NpcSettings _cfg;
        private ZoneGraph _graph;
        private IFireEngine _engine;
        private DoorInteractions _doors;
        private IPlayerRig _player;
        private float _postureThreshold;
        private Exposure _exposure;
        private GameObject _body;
        private readonly Queue<Vector3> _path = new Queue<Vector3>();
        private float _doorPause, _replan;
        private string _pendingDoor;
        private bool _crouched, _arrived;
        private Vector3? _destination;
        private Vector3 _elevatorSpot, _hideSpot;

        public string Id => _setup.Id;
        public string Name => _setup.Name;
        public Scenario.NpcSetup Setup => _setup;
        public NpcAction Action { get; private set; }
        public NpcResult Result { get; private set; }
        public bool Done => Result != NpcResult.Remained;
        /// <summary>Waiting for the brain: stand still and face the trainee.</summary>
        public bool Listening { get; set; }
        public float Fed => _exposure?.Fed ?? 0f;
        public float HeadHeight => _crouched ? _cfg.CrouchHeadM : _cfg.HeadM;
        public Vec3 Head => new Vec3(transform.position.x, HeadHeight, transform.position.z);
        public string Zone => _graph.ZoneAt(new Vec3(transform.position.x, 0.1f, transform.position.z));
        public float DistanceToPlayer => Flat(transform.position, Player) ;
        /// <summary>c at the head on the last tick, and its smoke level (0–3, 명세서 8장).</summary>
        public float CHead { get; private set; }

        private Vector3 Player => new Vector3(_player.Head.X, 0, _player.Head.Z);

        public void Init(Scenario.NpcSetup setup, RuntimeConfig rt, ZoneGraph graph, IFireEngine engine, DoorInteractions doors,
                         IPlayerRig player, EvaluationConfig evaluation, FireConfig fire, Font tagFont)
        {
            _setup = setup;
            _cfg = rt.Npc;
            _graph = graph;
            _engine = engine;
            _doors = doors;
            _player = player;
            _postureThreshold = evaluation.PostureThreshold;
            _fireCfg = fire;

            var shape = rt.Scene.Npc;
            _body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _body.name = "Body";
            _body.transform.SetParent(transform, false);
            _body.GetComponent<Collider>().isTrigger = true; // NPCs never block the trainee
            var r = _body.GetComponent<Renderer>();
            r.sharedMaterial = new Material(r.sharedMaterial);
            Materials.SetColor(r.sharedMaterial, Materials.Parse(shape.Color));
            _body.AddComponent<InteractTarget>().TargetId = setup.Id;
            _radius = shape.RadiusM;

            var tag = new GameObject("NameTag");
            tag.transform.SetParent(transform, false);
            var text = tag.AddComponent<TextMesh>();
            text.text = setup.Name;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            text.characterSize = _cfg.NameTagSize;
            text.fontSize = 48;
            if (tagFont != null)
            {
                text.font = tagFont;
                tag.GetComponent<MeshRenderer>().sharedMaterial = tagFont.material;
            }
            _tag = tag.transform;

            var button = graph.Zones.SelectMany(z => z.Objects ?? new List<BuildingObject>()).FirstOrDefault(o => o.Kind == ObjectKind.ElevatorButton);
            if (button != null)
            {
                var lobby = graph.Zone(graph.ZoneAt(button.Pos) ?? setup.HomeZone);
                var c = Centroid(lobby);
                var b = new Vector3(button.Pos.X, 0, button.Pos.Z);
                _elevatorSpot = b + (c - b).normalized * 0.6f;
            }
            _hideSpot = FarCorner(graph.Zone(setup.HomeZone));
            ResetForRun();
        }

        private FireConfig _fireCfg;
        private float _radius;
        private Transform _tag;

        public void ResetForRun()
        {
            transform.position = new Vector3(_setup.Spawn.X, 0, _setup.Spawn.Z);
            transform.rotation = Quaternion.identity;
            Action = _setup.InitialAction;
            Result = NpcResult.Remained;
            Listening = false;
            _exposure = new Exposure(_fireCfg);
            _crouched = false;
            _path.Clear();
            _destination = null;
            _arrived = false;
            _doorPause = 0;
            _pendingDoor = null;
            CHead = 0;
            ApplyPose();
        }

        public void SetAction(NpcAction action)
        {
            if (Done) return;
            if (action != Action) { _path.Clear(); _destination = null; _arrived = false; }
            Action = action;
        }

        /// <summary>Fire tick: take smoke like the trainee, without a towel.</summary>
        public void Tick(float dt)
        {
            if (Done) return;
            CHead = _engine.HeadConcentration(Head);
            _exposure.Accumulate(CHead, TowelState.None, dt);
            if (_exposure.Failed) Collapse();
        }

        private void Collapse()
        {
            Result = NpcResult.Collapsed;
            _path.Clear();
            ApplyPose();
        }

        /// <summary>Behaviour step; only called during the fire, not while paused.</summary>
        public void Step(float dt)
        {
            if (Done) { Billboard(); return; }
            if (Listening) { Face(Player); Billboard(); return; }
            switch (Action)
            {
                case NpcAction.Evacuate:
                    if (_path.Count == 0 && !_arrived) PlanToExit();
                    Walk(dt);
                    break;
                case NpcAction.FollowPlayer:
                    Follow(dt);
                    break;
                case NpcAction.GoElevator:
                    GoTo(_elevatorSpot, dt);
                    break;
                case NpcAction.Hide:
                    if (GoTo(_hideSpot, dt)) CloseHomeDoor();
                    break;
                default: // freeze, ask_help: stay
                    break;
            }
            var zone = Zone;
            if (zone != null && _graph.IsExit(zone)) { Result = NpcResult.Rescued; _path.Clear(); }
            Billboard();
        }

        // ---- behaviours ------------------------------------------------------

        private void Follow(float dt)
        {
            float d = DistanceToPlayer;
            if (d > _cfg.LoseDistanceM) { SetAction(NpcAction.Freeze); return; }
            _crouched = _player.StandingHeight > 0 && _player.Head.Y / _player.StandingHeight < _postureThreshold;
            ApplyPose();
            bool sameZone = Zone != null && Zone == _graph.ZoneAt(_player.Head);
            if (sameZone && d <= _cfg.FollowDistanceM && !InDoorway()) { _path.Clear(); Face(Player); return; }
            _replan -= dt;
            if (_replan <= 0f || _path.Count == 0)
            {
                _replan = _cfg.ReplanS;
                var target = Player;
                var zone = _graph.ZoneAt(_player.Head);
                Plan(zone, target, stopShortBy: sameZone ? _cfg.FollowDistanceM : 0f);
            }
            Walk(dt);
        }

        /// <returns>true when standing at the spot.</returns>
        private bool GoTo(Vector3 spot, float dt)
        {
            if (_arrived) return true;
            if (_destination != spot || _path.Count == 0)
            {
                _destination = spot;
                Plan(_graph.ZoneAt(new Vec3(spot.x, 0.1f, spot.z)), spot, 0f);
            }
            Walk(dt);
            if (_path.Count == 0 && Flat(transform.position, spot) <= _cfg.ArriveM * 2) _arrived = true;
            return _arrived;
        }

        private void CloseHomeDoor()
        {
            foreach (var e in _graph.EdgesOf(_setup.HomeZone))
                if (e.Type == EdgeType.Door && _doors.IsOpen(e.Id)) _doors.SetBySystem(e.Id, false);
        }

        /// <summary>The exit with the lower smoke-weighted path cost: length × (1 + smoke_path_cost × zone D).</summary>
        private void PlanToExit()
        {
            var from = new Vec3(transform.position.x, 0.1f, transform.position.z);
            IReadOnlyList<Vec3> best = null;
            float bestCost = float.MaxValue;
            foreach (var exit in _graph.Zones.Where(z => z.Type == ZoneType.Exit))
            {
                IReadOnlyList<Vec3> path;
                try { path = _graph.PathTo(from, exit.Id); } catch { continue; }
                if (path == null || path.Count == 0) continue;
                float cost = 0f;
                var prev = from;
                foreach (var p in path)
                {
                    var mid = new Vec3((prev.X + p.X) / 2, 0.1f, (prev.Z + p.Z) / 2);
                    var zone = _graph.ZoneAt(mid);
                    float d = zone != null ? _engine.Get(zone).D : 0f;
                    cost += Vec2.Distance(prev.Xz, p.Xz) * (1f + _cfg.SmokePathCost * d);
                    prev = p;
                }
                if (cost < bestCost) { bestCost = cost; best = path; }
            }
            _path.Clear();
            if (best == null) { _arrived = true; return; }
            foreach (var p in best) _path.Enqueue(new Vector3(p.X, 0, p.Z));
        }

        private void Plan(string zone, Vector3 target, float stopShortBy)
        {
            _path.Clear();
            if (zone == null) return;
            var from = new Vec3(transform.position.x, 0.1f, transform.position.z);
            IReadOnlyList<Vec3> path;
            try { path = _graph.PathTo(from, zone); } catch { return; }
            var points = path.Select(p => new Vector3(p.X, 0, p.Z)).ToList();
            if (points.Count > 0) points.RemoveAt(points.Count - 1); // the zone's spawn/centroid: go to the target instead
            var last = points.Count > 0 ? points[points.Count - 1] : transform.position;
            var dir = target - last;
            var end = stopShortBy > 0f && dir.magnitude > stopShortBy ? target - dir.normalized * stopShortBy : target;
            if (stopShortBy <= 0f || dir.magnitude > stopShortBy) points.Add(end);
            foreach (var p in points) _path.Enqueue(p);
        }

        private void Walk(float dt)
        {
            if (_doorPause > 0f)
            {
                _doorPause -= dt;
                if (_doorPause <= 0f && _pendingDoor != null) { _doors.SetBySystem(_pendingDoor, true); _pendingDoor = null; }
                return;
            }
            while (_path.Count > 0 && Flat(transform.position, _path.Peek()) <= _cfg.ArriveM) _path.Dequeue(); // already there
            if (_path.Count == 0) return;
            var target = _path.Peek();
            var door = DoorAt(target);
            if (door != null && !_doors.IsOpen(door) && Flat(transform.position, target) <= _radius + 0.4f)
            {
                _pendingDoor = door; // stop, open, then go through (16.9: 0.5 s, left open)
                _doorPause = _cfg.DoorPauseS;
                Face(target);
                return;
            }
            var pos = transform.position;
            var flat = new Vector3(target.x - pos.x, 0, target.z - pos.z);
            if (flat.magnitude <= _cfg.ArriveM) { _path.Dequeue(); return; }
            float speed = _cfg.WalkSpeedMps;
            transform.position = pos + Vector3.ClampMagnitude(flat.normalized * speed * dt, flat.magnitude);
            Face(target);
        }

        /// <summary>Standing in a door opening (within body radius + 0.35 m of its centre): do not stop there.</summary>
        private bool InDoorway() =>
            _graph.Edges.Any(e => e.Type == EdgeType.Door && Flat(transform.position, new Vector3(e.Center.X, 0, e.Center.Z)) < _radius + 0.35f);

        private string DoorAt(Vector3 p) =>
            _graph.Edges.FirstOrDefault(e => e.Type == EdgeType.Door && Mathf.Abs(e.Center.X - p.x) < 0.05f && Mathf.Abs(e.Center.Z - p.z) < 0.05f)?.Id;

        // ---- looks -----------------------------------------------------------

        private void ApplyPose()
        {
            if (_body == null) return;
            if (Result == NpcResult.Collapsed)
            {
                _body.transform.localRotation = Quaternion.Euler(90f, 0, 0);
                _body.transform.localPosition = new Vector3(0, _radius, 0);
                _body.transform.localScale = new Vector3(_radius * 2, _cfg.HeadM / 2f, _radius * 2);
                if (_tag != null) _tag.localPosition = new Vector3(0, _radius * 2 + _cfg.NameTagHeightM, 0);
                return;
            }
            float h = HeadHeight + 0.1f;
            _body.transform.localRotation = Quaternion.identity;
            _body.transform.localPosition = new Vector3(0, h / 2f, 0);
            _body.transform.localScale = new Vector3(_radius * 2, h / 2f, _radius * 2);
            if (_tag != null) _tag.localPosition = new Vector3(0, h + _cfg.NameTagHeightM, 0);
        }

        private void Face(Vector3 p)
        {
            var d = new Vector3(p.x - transform.position.x, 0, p.z - transform.position.z);
            if (d.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
        }

        private void Billboard()
        {
            var cam = Camera.main;
            if (_tag != null && cam != null) _tag.rotation = Quaternion.LookRotation(_tag.position - cam.transform.position, Vector3.up);
        }

        private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        private static Vector3 Centroid(Zone z) => new Vector3(z.Polygon.Average(p => p.X), 0, z.Polygon.Average(p => p.Z));

        /// <summary>Room corner farthest from its doors, pulled 0.5 m inside.</summary>
        private Vector3 FarCorner(Zone z)
        {
            var doors = _graph.EdgesOf(z.Id).Select(e => new Vector3(e.Center.X, 0, e.Center.Z)).ToList();
            var c = Centroid(z);
            var best = z.Polygon.Select(p => new Vector3(p.X, 0, p.Z))
                               .OrderByDescending(p => doors.Count == 0 ? 0 : doors.Min(d => Flat(p, d))).First();
            return best + (c - best).normalized * 0.5f;
        }
    }
}
