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

    // ZoneGraph (명세서 3장, 14.2, 16.8) is implemented in Core/Spatial/ZoneGraph.cs.
}
