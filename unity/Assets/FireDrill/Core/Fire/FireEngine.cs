using System;
using System.Collections.Generic;
using System.Linq;

namespace FireDrill.Core
{
    /// <summary>
    /// Zone smoke model (명세서 4장). Must match contracts/tools/ref_fire.py within 0.01 in D.
    ///
    /// One tick (dt = cfg.TickS):
    ///   1. door_events with at_s ≤ time since ignition change edge state;
    ///   2. origin zone D += source_rate·dt (capped at 1) while burning;
    ///   3. every flow uses the D values after step 2 (simultaneous update):
    ///        flow = k·width·(D_hi − D_lo)·dt; D_lo += flow·area_ref/A_lo; D_hi −= flow·area_ref/A_hi
    ///      k = cfg.EdgeK[edge] if present, else k[door_open|door_closed|opening|stair];
    ///   4. exit zones stay at 0; all D clamped to [0, 1].
    /// Blocked edges (scenario.blocked_edges, plus any blocked on the graph at Reset) carry no smoke.
    /// </summary>
    public sealed class FireEngine : IFireEngine
    {
        private ZoneGraph _graph;
        private FireConfig _cfg;
        private Scenario _scenario;

        private string[] _zoneIds;
        private Dictionary<string, int> _index;
        private double[] _d;
        private double[] _area;
        private bool[] _isExit;
        private Edge[] _flowEdges;
        private Dictionary<string, bool> _doorOpen;
        private List<Scenario.DoorEvent> _pendingEvents;
        private int _origin;

        private bool _ignited;
        private bool _extinguished;
        /// <summary>Seconds since ignition, advanced by dt per tick (rounded like the reference model).</summary>
        private double _tFire;

        public FirePhase Phase
        {
            get
            {
                if (!_ignited) return FirePhase.None;
                if (_extinguished) return FirePhase.Extinguished;
                if (_tFire < _cfg.PhasesS.InitialEnd - 1e-9) return FirePhase.Initial;
                if (_tFire < _cfg.PhasesS.GrowingEnd - 1e-9) return FirePhase.Growing;
                return FirePhase.Full;
            }
        }

        /// <summary>Seconds since ignition (0 before).</summary>
        public float TimeSinceIgnition => (float)_tFire;

        public void Reset(ZoneGraph graph, FireConfig cfg, Scenario scenario)
        {
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));

            _zoneIds = graph.Zones.Select(z => z.Id).ToArray();
            _index = new Dictionary<string, int>();
            for (int i = 0; i < _zoneIds.Length; i++) _index[_zoneIds[i]] = i;
            _d = new double[_zoneIds.Length];
            _area = _zoneIds.Select(id => graph.Area(id)).ToArray();
            _isExit = _zoneIds.Select(id => graph.IsExit(id)).ToArray();

            var blocked = new HashSet<string>(scenario.BlockedEdges ?? new List<string>());
            _flowEdges = graph.Edges.Where(e => !blocked.Contains(e.Id) && !graph.IsBlocked(e.Id)).ToArray();

            _doorOpen = new Dictionary<string, bool>();
            foreach (var e in graph.Edges)
                if (e.Type == EdgeType.Door) _doorOpen[e.Id] = e.DoorOpen ?? true;
            if (scenario.InitialDoors != null)
                foreach (var kv in scenario.InitialDoors) _doorOpen[kv.Key] = kv.Value;

            _pendingEvents = (scenario.DoorEvents ?? new List<Scenario.DoorEvent>()).OrderBy(ev => ev.AtS).ToList();

            string origin = scenario.Fire?.OriginZone ?? throw new ArgumentException("scenario.fire.origin_zone is required");
            if (!_index.TryGetValue(origin, out _origin)) throw new ArgumentException($"Unknown origin zone \"{origin}\"");

            _ignited = false;
            _extinguished = false;
            _tFire = 0;
        }

        public void Ignite(float t)
        {
            RequireReset();
            _ignited = true;
            _extinguished = false;
            _tFire = 0;
        }

        /// <summary>Advances one fixed step. t (run time) is not used: the model runs on time since ignition.</summary>
        public void Tick(float t, float dt)
        {
            RequireReset();
            if (!_ignited) return;

            while (_pendingEvents.Count > 0 && _pendingEvents[0].AtS <= _tFire + 1e-9)
            {
                var ev = _pendingEvents[0];
                _pendingEvents.RemoveAt(0);
                _doorOpen[ev.Edge] = ev.Action != Scenario.DoorAction.Close;
            }

            int n = _d.Length;
            var start = (double[])_d.Clone();
            if (!_extinguished) start[_origin] = Math.Min(1.0, start[_origin] + _cfg.SourceRate * dt);
            var next = (double[])start.Clone();
            double aref = _cfg.AreaRefM2;
            foreach (var e in _flowEdges)
            {
                int a = _index[e.From], b = _index[e.To];
                int hi = start[a] >= start[b] ? a : b;
                int lo = hi == a ? b : a;
                double flow = K(e) * e.Width * (start[hi] - start[lo]) * dt;
                next[lo] += flow * aref / _area[lo];
                next[hi] -= flow * aref / _area[hi];
            }
            for (int i = 0; i < n; i++) next[i] = _isExit[i] ? 0.0 : Math.Max(0.0, Math.Min(1.0, next[i]));
            _d = next;
            _tFire = Math.Round(_tFire + dt, 6);
        }

        private double K(Edge e)
        {
            if (_cfg.EdgeK != null && _cfg.EdgeK.TryGetValue(e.Id, out var over)) return over;
            switch (e.Type)
            {
                case EdgeType.Door: return IsDoorOpen(e.Id) ? _cfg.K.DoorOpen : _cfg.K.DoorClosed;
                case EdgeType.Opening: return _cfg.K.Opening;
                case EdgeType.Stair: return _cfg.K.Stair;
                default: throw new ArgumentOutOfRangeException(nameof(e), e.Type, null);
            }
        }

        public bool IsDoorOpen(string edgeId) => _doorOpen.TryGetValue(edgeId, out var open) ? open : true;

        public ZoneState Get(string zoneId)
        {
            RequireReset();
            if (!_index.TryGetValue(zoneId, out int i)) throw new KeyNotFoundException($"Unknown zone \"{zoneId}\"");
            double d = _d[i];
            double ceiling = _graph.Zone(zoneId).CeilingH;
            double layer = ceiling - d * (ceiling - _cfg.MinLayerY);
            double temp = _cfg.Temp.BaseC + _cfg.Temp.PerDC * d;
            if (i == _origin && _ignited && !_extinguished) temp += _cfg.Temp.OriginBonusC;
            return new ZoneState((float)d, (float)layer, (float)temp);
        }

        public void SetDoor(string edgeId, bool open)
        {
            RequireReset();
            if (_graph.Edge(edgeId).Type != EdgeType.Door) throw new ArgumentException($"Edge \"{edgeId}\" is not a door");
            _doorOpen[edgeId] = open;
        }

        public bool IsDoorHot(string edgeId, string fromZoneId)
        {
            RequireReset();
            var e = _graph.Edge(edgeId);
            if (e.From != fromZoneId && e.To != fromZoneId) throw new ArgumentException($"Zone \"{fromZoneId}\" is not on edge \"{edgeId}\"");
            return Get(ZoneGraph.Other(e, fromZoneId)).TempC >= _cfg.HotDoorTempC;
        }

        public float HeadConcentration(Vec3 head)
        {
            RequireReset();
            string zone = _graph.ZoneAt(head);
            if (zone == null) return 0f;
            var s = Get(zone);
            return head.Y > s.SmokeLayerY ? s.D : _cfg.BelowLayerFactor * s.D;
        }

        /// <summary>
        /// Only an Initial-phase fire within range can be put out, after cfg.ExtinguishNeededS of spraying.
        /// Smoke already made stays (no ventilation); the origin stops producing smoke and heat.
        /// </summary>
        public ExtinguishResult Extinguish(float t, float sprayedSeconds, float distance)
        {
            RequireReset();
            if (Phase == FirePhase.Extinguished) return ExtinguishResult.Extinguished;
            if (Phase != FirePhase.Initial || distance > _cfg.ExtinguishRangeM) return ExtinguishResult.Ineffective;
            if (sprayedSeconds + 1e-6 >= _cfg.ExtinguishNeededS)
            {
                _extinguished = true;
                return ExtinguishResult.Extinguished;
            }
            return ExtinguishResult.InProgress;
        }

        private void RequireReset()
        {
            if (_graph == null) throw new InvalidOperationException("FireEngine.Reset must be called first");
        }
    }
}
