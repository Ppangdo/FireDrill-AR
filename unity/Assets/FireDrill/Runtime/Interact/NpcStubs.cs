using System;
using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>Stage 4 NPC placeholders (사람 결정 2026-11-11): standing capsules at the scenario spawn points, and the
    /// talk → choice → NPC_INTERACT / SHOUT logging that the evaluation needs. Movement, reactions, the LLM and
    /// "ignore" detection come with stage 6 (NpcAgent, NpcBrain).</summary>
    public sealed class NpcStubs
    {
        private readonly IRunRecorder _recorder;
        private readonly IPlayerRig _rig;
        private readonly RuntimeConfig _rt;
        private readonly Dictionary<string, Scenario.NpcSetup> _npcs;
        private readonly Dictionary<string, GameObject> _bodies = new Dictionary<string, GameObject>();
        private readonly DialogueData _dialogue;
        private readonly GameFlow _flow;

        /// <summary>NPC whose choices are open, or null.</summary>
        public string Talking { get; private set; }
        public string TalkingName => Talking != null ? _npcs[Talking].Name : null;
        /// <summary>dialogue.json node of the open conversation: {npc}_fire during the fire, {npc}_idle before.</summary>
        public DialogueData.Node Node { get; private set; }
        public IReadOnlyList<DialogueData.Choice> Choices => (IReadOnlyList<DialogueData.Choice>)Node?.Choices ?? Array.Empty<DialogueData.Choice>();
        public string LastRefusal { get; private set; }
        public event Action<string> ChoicesOpened;

        public NpcStubs(Scenario scenario, DialogueData dialogue, RuntimeConfig rt, IRunRecorder recorder, IPlayerRig rig,
                        GameFlow flow, IInteractionSource input, Transform parent)
        {
            _dialogue = dialogue;
            _flow = flow;
            _recorder = recorder;
            _rig = rig;
            _rt = rt;
            _npcs = (scenario.Npcs ?? new List<Scenario.NpcSetup>()).ToDictionary(n => n.Id);
            var shape = rt.Scene.Npc;
            ColorUtility.TryParseHtmlString(shape.Color, out var color);
            foreach (var n in _npcs.Values)
            {
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = n.Id;
                body.transform.SetParent(parent, false);
                body.transform.position = new Vector3(n.Spawn.X, shape.HeightM / 2f, n.Spawn.Z);
                body.transform.localScale = new Vector3(shape.RadiusM * 2f, shape.HeightM / 2f, shape.RadiusM * 2f);
                body.GetComponent<Collider>().isTrigger = true; // placeholders do not block anyone
                var r = body.GetComponent<Renderer>();
                r.sharedMaterial = new Material(r.sharedMaterial) { color = color };
                if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", color);
                body.AddComponent<InteractTarget>().TargetId = n.Id;
                _bodies[n.Id] = body;
            }
            input.IntentRaised += i => { if (i.Kind == IntentKind.TalkTo) TalkTo(i.TargetId); };
            flow.RunStarted += _ => Close();
        }

        public bool IsNpc(string id) => id != null && _npcs.ContainsKey(id);

        private void TalkTo(string npcId)
        {
            if (!IsNpc(npcId)) { LastRefusal = "not an NPC: " + npcId; return; }
            var h = _rig.Head;
            var p = _bodies[npcId].transform.position;
            float d = Vector2.Distance(new Vector2(h.X, h.Z), new Vector2(p.x, p.z));
            if (d > _rt.Pc.TalkRangeM) { LastRefusal = $"{npcId} too far ({d:0.00} m)"; return; }
            Talking = npcId;
            var key = npcId + (_flow.State == FlowState.Fire ? "_fire" : "_idle");
            Node = _dialogue.Nodes.TryGetValue(key, out var node) ? node : null;
            ChoicesOpened?.Invoke(npcId);
        }

        /// <summary>A choice for the open conversation (number key, click, or a play script's "choose").
        /// warn / escort → NPC_INTERACT (warn also SHOUT method=choice, 명세서 8장); shout → SHOUT; anything else closes.</summary>
        public void Choose(string intent)
        {
            if (Talking == null) { LastRefusal = "no conversation open"; return; }
            var npc = Talking;
            if (Node?.Choices != null && !Node.Choices.Any(c => c.Intent == intent))
            {
                LastRefusal = $"'{intent}' is not a choice for {npc}";
                return;
            }
            Close();
            if (intent == "warn" || intent == "escort")
            {
                _recorder.Record(EventTypes.NpcInteract, new { npc, action = intent });
                if (intent == "warn") _recorder.Record(EventTypes.Shout, new { method = "choice" });
            }
            else if (intent == "shout") _recorder.Record(EventTypes.Shout, new { method = "choice" });
        }

        public void ChooseIndex(int index)
        {
            if (index >= 0 && index < Choices.Count) Choose(Choices[index].Intent);
        }

        public void Close()
        {
            Talking = null;
            Node = null;
        }
    }
}
