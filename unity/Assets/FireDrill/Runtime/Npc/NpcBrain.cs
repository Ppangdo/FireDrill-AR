using System;
using System.Collections.Generic;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    public enum NpcSource { Cache, Server, Fallback }

    /// <summary>명세서 8장 NPC reaction: ① local cache (npc_cache.json) → ② server /npc/react within npc_timeout_s →
    /// ③ a spare line from npc_cache.fallback with the action unchanged. Lines longer than 60 characters are refused.</summary>
    public sealed class NpcBrain
    {
        public const int MaxLine = 60;
        private readonly NpcCache _cache;
        private readonly ServerClient _server;
        private readonly Dictionary<string, int> _fallbackTurn = new Dictionary<string, int>();

        public int CacheHits { get; private set; }
        public int ServerAnswers { get; private set; }
        public int Fallbacks { get; private set; }

        public NpcBrain(NpcCache cache, ServerClient server)
        {
            _cache = cache;
            _server = server;
        }

        /// <param name="done">reaction, where it came from, seconds it took (main thread).</param>
        public void Decide(Scenario.NpcSetup npc, NpcAction current, PlayerAction player, int smokeLevel, string zone,
                           IReadOnlyList<string> recent, Action<NpcReaction, NpcSource, float> done)
        {
            float start = Time.realtimeSinceStartup;
            var key = NpcCache.Key(npc.Id, player, smokeLevel, current);
            if (_cache.Entries != null && _cache.Entries.TryGetValue(key, out var hit) && Valid(hit))
            {
                CacheHits++;
                done(hit, NpcSource.Cache, 0f);
                return;
            }
            var request = new NpcReactRequest
            {
                NpcId = npc.Id, Persona = npc.Persona, CurrentAction = current, Zone = zone, PlayerAction = player,
                SmokeLevel = Mathf.Clamp(smokeLevel, 0, 3), RecentEvents = new List<string>(recent ?? Array.Empty<string>()),
            };
            _server.ReactNpc(request, (res, err) =>
            {
                float took = Time.realtimeSinceStartup - start;
                if (res != null && res.Source == "llm")
                {
                    var r = new NpcReaction { Action = res.Action, Line = res.Line, Emotion = res.Emotion };
                    if (Valid(r)) { ServerAnswers++; done(r, NpcSource.Server, took); return; }
                }
                Fallbacks++;
                done(Fallback(npc.Id, current), NpcSource.Fallback, took);
            });
        }

        public NpcReaction Fallback(string npcId, NpcAction current)
        {
            var lines = _cache.Fallback != null && _cache.Fallback.TryGetValue(npcId, out var l) && l.Count > 0 ? l : new List<string> { "…" };
            _fallbackTurn.TryGetValue(npcId, out var turn);
            _fallbackTurn[npcId] = turn + 1;
            return new NpcReaction { Action = current, Line = lines[turn % lines.Count], Emotion = Emotion.Scared };
        }

        private static bool Valid(NpcReaction r) => r != null && r.Line != null && r.Line.Length <= MaxLine
                                                    && Enum.IsDefined(typeof(NpcAction), r.Action);
    }
}
