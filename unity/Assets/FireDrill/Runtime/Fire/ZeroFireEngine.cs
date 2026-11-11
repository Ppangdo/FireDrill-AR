using System.Collections.Generic;
using FireDrill.Core;

namespace FireDrill.Runtime
{
    /// <summary>Stage 3 stand-in (명세서 15장): no smoke anywhere, base temperature, doors never hot.
    /// Keeps door state so FireDirector and the real engine are interchangeable.</summary>
    public sealed class ZeroFireEngine : IFireEngine
    {
        private readonly Dictionary<string, float> _ceiling = new Dictionary<string, float>();
        private readonly Dictionary<string, bool> _doors = new Dictionary<string, bool>();
        private float _baseC;

        public FirePhase Phase { get; private set; } = FirePhase.None;
        public IReadOnlyDictionary<string, bool> Doors => _doors;

        public void Reset(ZoneGraph graph, FireConfig cfg, Scenario scenario)
        {
            _ceiling.Clear();
            _doors.Clear();
            foreach (var z in graph.Zones) _ceiling[z.Id] = z.CeilingH;
            foreach (var e in graph.Edges)
                if (e.Type == EdgeType.Door) _doors[e.Id] = e.DoorOpen ?? false;
            if (scenario.InitialDoors != null)
                foreach (var kv in scenario.InitialDoors) _doors[kv.Key] = kv.Value;
            _baseC = cfg.Temp?.BaseC ?? 0f;
            Phase = FirePhase.None;
        }

        public void Ignite(float t) => Phase = FirePhase.Initial;
        public void Tick(float t, float dt) { }
        public ZoneState Get(string zoneId) =>
            new ZoneState(0f, zoneId != null && _ceiling.TryGetValue(zoneId, out var h) ? h : 0f, _baseC);
        public void SetDoor(string edgeId, bool open) => _doors[edgeId] = open;
        public bool IsDoorHot(string edgeId, string fromZoneId) => false;
        public float HeadConcentration(Vec3 head) => 0f;
        public ExtinguishResult Extinguish(float t, float sprayedSeconds, float distance) => ExtinguishResult.Ineffective;
    }
}
