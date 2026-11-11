using System.Collections.Generic;

namespace FireDrill.Core
{
    public enum NpcAction { Evacuate, FollowPlayer, Freeze, AskHelp, GoElevator, Hide }
    public enum CueKind { Sound, Smell, Smoke, Alarm }

    /// <summary>scenario.json (contracts/schemas/scenario.schema.json).</summary>
    public sealed class Scenario
    {
        public int SchemaVersion { get; set; }
        public string Id { get; set; }
        public string BuildingId { get; set; }
        public SpawnPoint PlayerSpawn { get; set; }
        public SpawnPoint RetrySpawn { get; set; }
        public FireSetup Fire { get; set; }
        public Dictionary<string, bool> InitialDoors { get; set; }
        public List<DoorEvent> DoorEvents { get; set; } = new List<DoorEvent>();
        public List<string> BlockedEdges { get; set; }
        public List<CueAt> Cues { get; set; }
        public TutorialSetup Tutorial { get; set; }
        public List<NpcSetup> Npcs { get; set; }
        /// <summary>What fire.json must achieve here; checked by contracts/tools/ref_fire.py and the Stage 1 engine tests.</summary>
        public List<TuningTarget> TuningTargets { get; set; } = new List<TuningTarget>();
        public float TimeoutS { get; set; }

        public enum DoorAction { BurstOpen, Open, Close }
        public sealed class DoorEvent { public string Edge { get; set; } public float AtS { get; set; } public DoorAction Action { get; set; } }

        /// <summary>Tutorial ends when the player enters ReturnZone holding ErrandObject (or after MaxS).</summary>
        public sealed class TutorialSetup
        {
            public string StartNode { get; set; }
            public string ErrandObject { get; set; }
            public string ReturnZone { get; set; }
            public string ReturnNode { get; set; }
            public float MaxS { get; set; }
        }

        public enum TargetKind { ReachBelow, StayAbove }
        public sealed class TuningTarget
        {
            public string Zone { get; set; }
            public TargetKind Kind { get; set; }
            public float HeightM { get; set; }
            public float AtS { get; set; }
            public float? ToleranceS { get; set; }
            public string Why { get; set; }
        }

        public sealed class SpawnPoint { public Vec3 Pos { get; set; } public float Yaw { get; set; } }
        public sealed class FireSetup { public string OriginZone { get; set; } public float FireAtS { get; set; } public float? RetryFireAtS { get; set; } }
        public sealed class CueAt { public CueKind Cue { get; set; } public float AtS { get; set; } }
        public sealed class NpcSetup
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Persona { get; set; }
            public Vec3 Spawn { get; set; }
            public string HomeZone { get; set; }
            public NpcAction InitialAction { get; set; }
        }
    }

    /// <summary>dialogue.json (contracts/schemas/dialogue.schema.json).</summary>
    public sealed class DialogueData
    {
        public int SchemaVersion { get; set; }
        public string Start { get; set; }
        public Dictionary<string, Node> Nodes { get; set; }

        public sealed class Node
        {
            public string Speaker { get; set; }
            public string Text { get; set; }
            public string Next { get; set; }
            public List<Choice> Choices { get; set; }
            public OnEnd OnEnd { get; set; }
        }

        /// <summary>intent: accept_errand | ok | warn | escort | shout | ask. "warn" also records SHOUT(method=choice).</summary>
        public sealed class Choice { public string Text { get; set; } public string Intent { get; set; } public string Next { get; set; } }
        public sealed class OnEnd { public string Quest { get; set; } public string Flow { get; set; } }
    }

    /// <summary>app_config.json.</summary>
    public sealed class AppConfig
    {
        public int SchemaVersion { get; set; }
        public string Platform { get; set; }
        public string ServerUrl { get; set; }
        public string DeviceId { get; set; }
        public string DeviceToken { get; set; }
        public string BuildingId { get; set; }
        public string ScenarioId { get; set; }
        public bool Offline { get; set; }
        /// <summary>PC: true (standing head = evaluation.standing_head_m). Quest: false.</summary>
        public bool SkipCalibration { get; set; } = true;
        public string Language { get; set; }
    }
}
