using System;
using System.Collections.Generic;

namespace FireDrill.Core
{
    // ── zone_graph.json DTOs (contracts/schemas/zone_graph.schema.json) ──────────

    public enum ZoneType { Room, Corridor, Stair, ElevatorLobby, Exit, Refuge }
    public enum EdgeType { Door, Opening, Stair }
    public enum WallAxis { X, Z }
    public enum ObjectKind { Towel, Extinguisher, Phone, Document, Bag, Alarm, Faucet, ElevatorButton, Window }

    public sealed class ZoneGraphData
    {
        public int SchemaVersion { get; set; }
        public string BuildingId { get; set; }
        public int Version { get; set; }
        public Dictionary<string, Vec3> Anchors { get; set; }
        public List<Zone> Zones { get; set; }
        public List<Edge> Edges { get; set; }
    }

    public sealed class Zone
    {
        public string Id { get; set; }
        public ZoneType Type { get; set; }
        public string Name { get; set; }
        public List<Vec2> Polygon { get; set; }
        public float CeilingH { get; set; }
        public Vec3? Spawn { get; set; }
        public List<BuildingObject> Objects { get; set; }
    }

    public sealed class BuildingObject
    {
        public string Id { get; set; }
        public ObjectKind Kind { get; set; }
        public Vec3 Pos { get; set; }
    }

    public sealed class Edge
    {
        public string Id { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public EdgeType Type { get; set; }
        public float Width { get; set; }
        public Vec2 Center { get; set; }
        public WallAxis WallAxis { get; set; }
        public bool? DoorOpen { get; set; }
        public Vec3? Handle { get; set; }
        public string SideFromHandle { get; set; }
    }

    public sealed class ZoneGraphException : Exception
    {
        public ZoneGraphException(string message) : base(message) { }
    }

    /// <summary>
    /// Spatial model shared by fire engine, evaluator and NPCs (명세서 3장, 14.2, 16.8).
    /// Stage 0: contract only. Stage 1 implements every member.
    /// </summary>
    public sealed class ZoneGraph
    {
        public ZoneGraphData Data { get; }
        private ZoneGraph(ZoneGraphData data) { Data = data; }

        /// <summary>Parse and validate. Throws ZoneGraphException naming the bad field path.</summary>
        public static ZoneGraph Load(string json) => throw new NotImplementedException("Stage 1");

        public IReadOnlyList<Zone> Zones => Data.Zones;
        public IReadOnlyList<Edge> Edges => Data.Edges;

        public Zone Zone(string zoneId) => throw new NotImplementedException("Stage 1");
        public IEnumerable<Edge> EdgesOf(string zoneId) => throw new NotImplementedException("Stage 1");
        public bool IsExit(string zoneId) => throw new NotImplementedException("Stage 1");

        /// <summary>Zone whose floor polygon contains pos (x, z), or null.</summary>
        public string ZoneAt(Vec3 pos) => throw new NotImplementedException("Stage 1");

        public void SetBlocked(string edgeId, bool blocked) => throw new NotImplementedException("Stage 1");

        /// <summary>
        /// Continuous distance to the nearest exit (명세서 16.8): 0 inside an exit zone; otherwise
        /// min over edges e of the current zone of |pos.xz − e.center| + D(e), where D(e) is the shortest
        /// edge-centre path from e to any edge touching an exit zone (D = 0 there). Blocked edges excluded,
        /// closed doors passable. Null if pos is outside every zone.
        /// Reference implementation: contracts/tools/ref_eval.py ZoneGraph.exit_distance.
        /// </summary>
        public float? ExitDistance(Vec3 pos) => throw new NotImplementedException("Stage 1");

        /// <summary>Waypoints (edge centres, then the target) for NPC walking.</summary>
        public IReadOnlyList<Vec3> PathToExit(Vec3 from) => throw new NotImplementedException("Stage 1");
        public IReadOnlyList<Vec3> PathTo(Vec3 from, string zoneId) => throw new NotImplementedException("Stage 1");
    }
}
