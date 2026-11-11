using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FireDrill.Core
{
    /// <summary>Event type strings (명세서 6장). JSON keeps them UPPER_SNAKE.</summary>
    public static class EventTypes
    {
        public const string RunStart = "RUN_START";
        public const string RunEnd = "RUN_END";
        public const string StateChange = "STATE_CHANGE";
        public const string CueExposed = "CUE_EXPOSED";
        public const string ZoneEnter = "ZONE_ENTER";
        public const string Posture = "POSTURE";
        public const string Sample = "SAMPLE";
        public const string HandleCheck = "HANDLE_CHECK";
        public const string DoorOpen = "DOOR_OPEN";
        public const string DoorClose = "DOOR_CLOSE";
        public const string AlarmPressed = "ALARM_PRESSED";
        public const string Shout = "SHOUT";
        public const string Pickup = "PICKUP";
        public const string Use = "USE";
        public const string TowelWet = "TOWEL_WET";
        public const string TowelEquip = "TOWEL_EQUIP";
        public const string Extinguish = "EXTINGUISH";
        public const string ElevatorCall = "ELEVATOR_CALL";
        public const string WindowSignal = "WINDOW_SIGNAL";
        public const string NpcInteract = "NPC_INTERACT";
        public const string CrowdFollow = "CROWD_FOLLOW";
        public const string BodyStage = "BODY_STAGE";
        public const string Abort = "ABORT";

        public static readonly IReadOnlyCollection<string> All = new[]
        {
            RunStart, RunEnd, StateChange, CueExposed, ZoneEnter, Posture, Sample, HandleCheck, DoorOpen, DoorClose,
            AlarmPressed, Shout, Pickup, Use, TowelWet, TowelEquip, Extinguish, ElevatorCall, WindowSignal,
            NpcInteract, CrowdFollow, BodyStage, Abort,
        };
    }

    public enum Outcome { Escaped, Rescued, Failed, Timeout, Aborted }

    /// <summary>Player state copied into every event (명세서 6장 공통 필드 + zone_d, layer_y).</summary>
    public readonly struct Snapshot
    {
        public readonly float T;
        public readonly string Zone;
        public readonly Vec3 Pos;
        public readonly float HeadRatio;
        public readonly float CHead;
        public readonly float Fed;
        public readonly float ZoneD;
        public readonly float LayerY;

        public Snapshot(float t, string zone, Vec3 pos, float headRatio, float cHead, float fed, float zoneD, float layerY)
        {
            T = t; Zone = zone; Pos = pos; HeadRatio = headRatio; CHead = cHead; Fed = fed; ZoneD = zoneD; LayerY = layerY;
        }
    }

    /// <summary>One JSONL line (contracts/schemas/log_event.schema.json). Flat on the wire.</summary>
    public sealed class LogEvent
    {
        public string RunId { get; set; }
        public int Seq { get; set; }
        public float T { get; set; }
        public string Type { get; set; }
        public string Zone { get; set; }
        public Vec3 Pos { get; set; }
        public float HeadRatio { get; set; }
        public float CHead { get; set; }
        public float Fed { get; set; }
        public float ZoneD { get; set; }
        public float LayerY { get; set; }
        public JObject Data { get; set; } = new JObject();

        public LogEvent() { }

        public LogEvent(string runId, int seq, string type, Snapshot s, JObject data)
        {
            RunId = runId; Seq = seq; Type = type; T = s.T; Zone = s.Zone; Pos = s.Pos; HeadRatio = s.HeadRatio;
            CHead = s.CHead; Fed = s.Fed; ZoneD = s.ZoneD; LayerY = s.LayerY; Data = data ?? new JObject();
        }

        [JsonIgnore]
        public Snapshot Snapshot => new Snapshot(T, Zone, Pos, HeadRatio, CHead, Fed, ZoneD, LayerY);

        public static LogEvent ParseLine(string line) => FireDrillJson.Deserialize<LogEvent>(line);
        public string ToLine() => FireDrillJson.Serialize(this);
    }

    public interface IEventLog
    {
        string RunId { get; }
        /// <summary>Appends with the next seq (starting at 1). data: anonymous object, dictionary or JObject.</summary>
        LogEvent Append(string type, Snapshot s, object data = null);
        IReadOnlyList<LogEvent> Events { get; }
        /// <summary>Subscribed by the file writer and the server uploader.</summary>
        event Action<LogEvent> Appended;
    }
}
