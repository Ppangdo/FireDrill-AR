using System;
using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>NPCs in the run (명세서 8장, 16.9): creates the agents, opens the talk → choice dialogue
    /// (dialogue.json {npc}_fire / _idle), logs NPC_INTERACT (warn also SHOUT method=choice), asks the NpcBrain for the
    /// next action on a choice or when the NPC's smoke level changes, detects "ignore" (6장), ticks their exposure and
    /// reports rescued / collapsed / remained at the end of the run.</summary>
    public sealed class NpcDirector
    {
        private readonly Scenario _scenario;
        private readonly DialogueData _dialogue;
        private readonly RuntimeConfig _rt;
        private readonly IRunRecorder _recorder;
        private readonly IPlayerRig _rig;
        private readonly GameFlow _flow;
        private readonly ZoneGraph _graph;
        private readonly NpcBrain _brain;
        private readonly Dictionary<string, NpcAgent> _agents = new Dictionary<string, NpcAgent>();
        private readonly Dictionary<string, int> _smoke = new Dictionary<string, int>();
        private readonly Dictionary<string, float> _near = new Dictionary<string, float>();
        private readonly Dictionary<string, bool> _spokenThisEncounter = new Dictionary<string, bool>();
        private readonly HashSet<string> _helped = new HashSet<string>(); // warned or escorted this run
        private readonly Dictionary<string, List<string>> _recent = new Dictionary<string, List<string>>();

        public IReadOnlyDictionary<string, NpcAgent> Agents => _agents;
        public NpcBrain Brain => _brain;
        /// <summary>NPC whose choices are open, or null.</summary>
        public string Talking { get; private set; }
        public string TalkingName => Talking != null ? _agents[Talking].Name : null;
        public DialogueData.Node Node { get; private set; }
        public IReadOnlyList<DialogueData.Choice> Choices => (IReadOnlyList<DialogueData.Choice>)Node?.Choices ?? Array.Empty<DialogueData.Choice>();
        public string LastRefusal { get; private set; }
        /// <summary>(npc, line) spoken: shown as a subtitle.</summary>
        public event Action<NpcAgent, string> Said;
        /// <summary>Results of the last finished run, by NPC id.</summary>
        public IReadOnlyDictionary<string, NpcResult> Results { get; private set; } = new Dictionary<string, NpcResult>();

        public NpcDirector(ConfigSet config, IFireEngine engine, DoorInteractions doors, NpcBrain brain, IRunRecorder recorder,
                           IPlayerRig rig, GameFlow flow, FireDirector fire, IInteractionSource input, Transform parent)
        {
            _scenario = config.Scenario;
            _dialogue = config.Dialogue;
            _rt = config.Runtime;
            _recorder = recorder;
            _rig = rig;
            _flow = flow;
            _graph = config.Graph;
            _brain = brain;
            Font tagFont = null;
            try { tagFont = Font.CreateDynamicFontFromOSFont(_rt.Ui.FallbackOsFont, 48); } catch { /* name tags fall back to the default font */ }
            foreach (var n in _scenario.Npcs ?? new List<Scenario.NpcSetup>())
            {
                var go = new GameObject(n.Id);
                go.transform.SetParent(parent, false);
                var agent = go.AddComponent<NpcAgent>();
                agent.Init(n, _rt, _graph, engine, doors, rig, config.Evaluation, config.Fire, tagFont);
                _agents[n.Id] = agent;
            }
            input.IntentRaised += i => { if (i.Kind == IntentKind.TalkTo) TalkTo(i.TargetId); };
            flow.RunStarted += _ => ResetForRun();
            fire.Ticked += (t, dt) => OnTick(dt);
        }

        public bool IsNpc(string id) => id != null && _agents.ContainsKey(id);

        private void ResetForRun()
        {
            Close();
            _smoke.Clear();
            _near.Clear();
            _spokenThisEncounter.Clear();
            _helped.Clear();
            _recent.Clear();
            foreach (var a in _agents.Values) a.ResetForRun();
            Results = new Dictionary<string, NpcResult>();
        }

        // ---- per frame / per tick ------------------------------------------------

        /// <summary>Called by Bootstrap every frame with the scaled delta time.</summary>
        public void Update(float dt)
        {
            if (!_flow.RunActive || _flow.Paused || _flow.State != FlowState.Fire) return;
            foreach (var a in _agents.Values)
            {
                a.Step(dt);
                DetectIgnore(a, dt);
            }
        }

        private void OnTick(float dt)
        {
            if (_flow.State != FlowState.Fire) return;
            foreach (var a in _agents.Values)
            {
                a.Tick(dt);
                if (a.Done || a.Listening) continue;
                int level = SmokeLevel.From(a.CHead);
                if (_smoke.TryGetValue(a.Id, out var last) && last == level) continue;
                bool first = !_smoke.ContainsKey(a.Id);
                _smoke[a.Id] = level;
                if (!first) Ask(a, PlayerAction.None); // 8장: the NPC's smoke level changed
            }
        }

        /// <summary>6장 "지나침": the trainee stayed within ignore_radius_m for ignore_s and left without interacting.</summary>
        private void DetectIgnore(NpcAgent a, float dt)
        {
            if (a.Done || _helped.Contains(a.Id)) return; // already helped: walking away from a follower is not ignoring
            float d = a.DistanceToPlayer;
            if (d <= _rt.Npc.IgnoreRadiusM)
            {
                _near.TryGetValue(a.Id, out var t);
                _near[a.Id] = t + dt;
                return;
            }
            if (_near.TryGetValue(a.Id, out var stayed))
            {
                _near.Remove(a.Id);
                bool spoke = _spokenThisEncounter.TryGetValue(a.Id, out var s) && s;
                _spokenThisEncounter[a.Id] = false;
                if (stayed >= _rt.Npc.IgnoreS && !spoke)
                    _recorder.Record(EventTypes.NpcInteract, new { npc = a.Id, action = "ignore" });
            }
        }

        // ---- dialogue ------------------------------------------------------------

        private void TalkTo(string npcId)
        {
            if (!IsNpc(npcId)) { LastRefusal = "not an NPC: " + npcId; return; }
            var a = _agents[npcId];
            if (a.Result == NpcResult.Collapsed) { LastRefusal = a.Name + " cannot answer"; return; }
            float d = a.DistanceToPlayer;
            if (d > _rt.Pc.TalkRangeM) { LastRefusal = $"{npcId} too far ({d:0.00} m)"; return; }
            Talking = npcId;
            var key = npcId + (_flow.State == FlowState.Fire ? "_fire" : "_idle");
            Node = _dialogue.Nodes.TryGetValue(key, out var node) ? node : null;
            if (Node != null) Said?.Invoke(a, Node.Text);
            if (Node?.Choices == null || Node.Choices.Count == 0) Talking = null; // just a line
            else a.Listening = true;
        }

        /// <summary>A choice for the open conversation (number key, click, or a play script's "choose").</summary>
        public void Choose(string intent)
        {
            if (Talking == null) { LastRefusal = "no conversation open"; return; }
            if (Node?.Choices != null && !Node.Choices.Any(c => c.Intent == intent))
            {
                LastRefusal = $"'{intent}' is not a choice for {Talking}";
                return;
            }
            var a = _agents[Talking];
            Close();
            _spokenThisEncounter[a.Id] = true;
            if (intent == "warn" || intent == "escort")
            {
                _helped.Add(a.Id);
                _recorder.Record(EventTypes.NpcInteract, new { npc = a.Id, action = intent });
                if (intent == "warn") _recorder.Record(EventTypes.Shout, new { method = "choice" });
                Remember(a.Id, intent == "warn" ? "훈련자가 불이 났다고 알림" : "훈련자가 같이 가자고 함");
                Ask(a, intent == "warn" ? PlayerAction.Warn : PlayerAction.Escort);
            }
            else
            {
                a.Listening = false;
                if (intent == "shout") _recorder.Record(EventTypes.Shout, new { method = "choice" });
            }
        }

        public void ChooseIndex(int index)
        {
            if (index >= 0 && index < Choices.Count) Choose(Choices[index].Intent);
        }

        public void Close()
        {
            if (Talking != null && _agents.TryGetValue(Talking, out var a)) a.Listening = false;
            Talking = null;
            Node = null;
        }

        private void Ask(NpcAgent a, PlayerAction player)
        {
            a.Listening = true;
            var current = a.Action;
            _brain.Decide(a.Setup, current, player, SmokeLevel.From(a.CHead), a.Zone, Recent(a.Id), (reaction, source, took) =>
            {
                a.Listening = false;
                if (a.Done) return;
                a.SetAction(reaction.Action);
                if (!string.IsNullOrEmpty(reaction.Line)) Said?.Invoke(a, reaction.Line);
                Debug.Log($"[FireDrill] {a.Id} {player}/{current} → {reaction.Action} ({source}, {took:0.00}s)");
            });
        }

        private void Remember(string npcId, string text)
        {
            if (!_recent.TryGetValue(npcId, out var list)) _recent[npcId] = list = new List<string>();
            list.Add(text);
            if (list.Count > 10) list.RemoveAt(0);
        }

        private IReadOnlyList<string> Recent(string npcId) => _recent.TryGetValue(npcId, out var l) ? l : (IReadOnlyList<string>)Array.Empty<string>();

        // ---- results -------------------------------------------------------------

        /// <summary>16.9 results at the end of a run. Followers still within lose_distance_m of a trainee who escaped
        /// walk out with them and count as rescued (the run ends the moment the trainee reaches the exit).</summary>
        public IReadOnlyDictionary<string, NpcResult> FinishRun(Outcome outcome)
        {
            var results = new Dictionary<string, NpcResult>();
            foreach (var a in _agents.Values)
            {
                var r = a.Result;
                if (r == NpcResult.Remained && outcome == Outcome.Escaped && a.Action == NpcAction.FollowPlayer
                    && a.DistanceToPlayer <= _rt.Npc.LoseDistanceM)
                    r = NpcResult.Rescued;
                results[a.Id] = r;
            }
            Results = results;
            Close();
            return results;
        }
    }
}
