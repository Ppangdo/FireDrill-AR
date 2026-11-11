using System;
using System.Collections.Generic;

namespace FireDrill.Core
{
    // ── fire.json DTO ────────────────────────────────────────────────────────────

    public sealed class FireConfig
    {
        public int SchemaVersion { get; set; }
        public float TickS { get; set; }
        public float SourceRate { get; set; }
        public float AreaRefM2 { get; set; }
        public SpreadK K { get; set; }
        /// <summary>Per-edge spread coefficient; overrides K for that edge (door state ignored).</summary>
        public Dictionary<string, float> EdgeK { get; set; } = new Dictionary<string, float>();
        public float MinLayerY { get; set; }
        public TempConfig Temp { get; set; }
        public float HotDoorTempC { get; set; }
        public float BelowLayerFactor { get; set; }
        public PhaseTimes PhasesS { get; set; }
        public float ExtinguishNeededS { get; set; }
        public float ExtinguishRangeM { get; set; }
        public FedConfig Fed { get; set; }
        public RenderConfig Render { get; set; }

        public sealed class SpreadK { public float DoorOpen { get; set; } public float DoorClosed { get; set; } public float Opening { get; set; } public float Stair { get; set; } }
        public sealed class TempConfig
        {
            public float BaseC { get; set; }
            [Newtonsoft.Json.JsonProperty("per_d_c")] public float PerDC { get; set; }
            public float OriginBonusC { get; set; }
        }
        public sealed class PhaseTimes { public float InitialEnd { get; set; } public float GrowingEnd { get; set; } }
        public sealed class TowelFactor { public float None { get; set; } public float Dry { get; set; } public float Wet { get; set; } }
        public sealed class FedConfig { public float FullDoseS { get; set; } public TowelFactor Towel { get; set; } public List<float> Stages { get; set; } }
        public sealed class RenderConfig { public float MaxOpacity { get; set; } }
    }

    // ── engine contract (명세서 4장, 14.2) ──────────────────────────────────────

    public enum FirePhase { None, Initial, Growing, Full, Extinguished }
    public enum ExtinguishResult { Ineffective, InProgress, Extinguished }

    public readonly struct ZoneState
    {
        /// <summary>Smoke density 0..1.</summary>
        public readonly float D;
        /// <summary>Bottom of the smoke layer above the floor (m).</summary>
        public readonly float SmokeLayerY;
        public readonly float TempC;
        public ZoneState(float d, float smokeLayerY, float tempC) { D = d; SmokeLayerY = smokeLayerY; TempC = tempC; }
    }

    public interface IFireEngine
    {
        void Reset(ZoneGraph graph, FireConfig cfg, Scenario scenario);
        /// <summary>Called by GameFlow on entering Fire. t = run time.</summary>
        void Ignite(float t);
        /// <summary>Fixed step, dt = cfg.TickS (0.1). Caller accumulates frame time.</summary>
        void Tick(float t, float dt);
        FirePhase Phase { get; }
        ZoneState Get(string zoneId);
        void SetDoor(string edgeId, bool open);
        /// <summary>True if the zone on the other side of the edge (seen from fromZoneId) has TempC ≥ cfg.HotDoorTempC.</summary>
        bool IsDoorHot(string edgeId, string fromZoneId);
        /// <summary>c in 명세서 4장: D if head is inside the layer, else BelowLayerFactor × D.</summary>
        float HeadConcentration(Vec3 head);
        ExtinguishResult Extinguish(float t, float sprayedSeconds, float distance);
    }

    // ── exposure (명세서 4장 FED) ────────────────────────────────────────────────

    public enum TowelState { None, Dry, Wet }

    // Exposure (명세서 4장 FED) is implemented in Core/Fire/Exposure.cs.
}
