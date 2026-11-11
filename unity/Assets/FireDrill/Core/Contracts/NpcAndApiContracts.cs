using System;
using System.Collections.Generic;

namespace FireDrill.Core
{
    public enum PlayerAction { Warn, Escort, Ignore, None }
    public enum Emotion { Calm, Scared, Panicked, Relieved, Stubborn }

    /// <summary>npc_cache.json. Key = "npc_id|player_action|smoke_level|current_action".</summary>
    public sealed class NpcCache
    {
        public int SchemaVersion { get; set; }
        public string Model { get; set; }
        public string GeneratedAt { get; set; }
        public Dictionary<string, NpcReaction> Entries { get; set; }
        public Dictionary<string, List<string>> Fallback { get; set; }

        public static string Key(string npcId, PlayerAction player, int smokeLevel, NpcAction current) =>
            $"{npcId}|{Snake(player.ToString())}|{smokeLevel}|{Snake(current.ToString())}";

        private static string Snake(string pascal)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < pascal.Length; i++)
            {
                char c = pascal[i];
                if (char.IsUpper(c) && i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }

    public sealed class NpcReaction
    {
        public NpcAction Action { get; set; }
        public string Line { get; set; }
        public Emotion Emotion { get; set; }
    }

    /// <summary>Smoke level for NPC prompts and cache keys (명세서 8장).</summary>
    public static class SmokeLevel
    {
        public static int From(float c) => c < 0.1f ? 0 : c < 0.3f ? 1 : c < 0.6f ? 2 : 3;
    }

    // ── server API bodies (명세서 14.6; Python twin: server/app/schemas.py) ───────

    public sealed class HealthResponse { public int SchemaVersion { get; set; } public bool Ok { get; set; } public string Llm { get; set; } public string ServerTime { get; set; } }

    public sealed class CreateSessionRequest { public int SchemaVersion { get; set; } = 1; public string BuildingId { get; set; } public string ParticipantCode { get; set; } public string DeviceId { get; set; } }
    public sealed class CreateSessionResponse { public int SchemaVersion { get; set; } public Guid SessionId { get; set; } }

    public sealed class CreateRunRequest { public int SchemaVersion { get; set; } = 1; public string ScenarioId { get; set; } public int Attempt { get; set; } }
    public sealed class CreateRunResponse { public int SchemaVersion { get; set; } public Guid RunId { get; set; } }

    public sealed class PostEventsRequest { public int SchemaVersion { get; set; } = 1; public List<LogEvent> Events { get; set; } = new List<LogEvent>(); }
    public sealed class PostEventsResponse { public int SchemaVersion { get; set; } public int Accepted { get; set; } public int LastSeq { get; set; } }

    public sealed class FinishRunRequest { public int SchemaVersion { get; set; } = 1; public Outcome Outcome { get; set; } public EvaluationResult Evaluation { get; set; } }
    public sealed class OkResponse { public int SchemaVersion { get; set; } public bool Ok { get; set; } }

    public sealed class ReportRequest { public int SchemaVersion { get; set; } = 1; public EvaluationResult Evaluation { get; set; } }
    public sealed class ReportResponse { public int SchemaVersion { get; set; } public ReportText Report { get; set; } public string Model { get; set; } public bool FallbackUsed { get; set; } public int LatencyMs { get; set; } }

    public sealed class NpcReactRequest
    {
        public int SchemaVersion { get; set; } = 1;
        public string NpcId { get; set; }
        public string Persona { get; set; }
        public NpcAction CurrentAction { get; set; }
        public string Zone { get; set; }
        public PlayerAction PlayerAction { get; set; }
        public int SmokeLevel { get; set; }
        public List<string> RecentEvents { get; set; } = new List<string>();
    }

    public sealed class NpcReactResponse
    {
        public int SchemaVersion { get; set; }
        public NpcAction Action { get; set; }
        public string Line { get; set; }
        public Emotion Emotion { get; set; }
        /// <summary>"llm" | "fallback"</summary>
        public string Source { get; set; }
    }

    public sealed class ErrorResponse
    {
        public ErrorBody Error { get; set; }
        /// <summary>code: bad_token | not_found | invalid | llm_timeout | llm_error</summary>
        public sealed class ErrorBody { public string Code { get; set; } public string Message { get; set; } }
    }

    // WebSocket /ws/device/{device_id}?token=...
    public sealed class WsStatus { public string Type { get; set; } = "status"; public string RunId { get; set; } public string State { get; set; } public float T { get; set; } public string Zone { get; set; } public float Fed { get; set; } public int Stage { get; set; } }
    public sealed class WsAbort { public string Type { get; set; } = "abort"; public string By { get; set; } }
    public sealed class WsPing { public string Type { get; set; } = "ping"; }
}
