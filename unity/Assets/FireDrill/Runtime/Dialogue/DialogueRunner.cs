using System;
using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>명세서 5장 Tutorial + 14.4 DialogueRunner: runs dialogue.json nodes that are not NPC conversations.
    /// Tutorial: start_node on entering Tutorial → on_end quest fetch_document → when the errand object is held and the
    /// trainee is in return_zone, return_node → on_end flow to_prefire → GameFlow.TutorialDone. tutorial.max_s also ends it.
    /// A line without choices stays ui.line_s seconds or until Continue. The game never pauses for dialogue.</summary>
    public sealed class DialogueRunner
    {
        private readonly DialogueData _data;
        private readonly Scenario.TutorialSetup _tutorial;
        private readonly GameFlow _flow;
        private readonly Inventory _inventory;
        private readonly ZoneGraph _graph;
        private readonly IPlayerRig _rig;
        private readonly float _lineS;
        private float _lineLeft, _tutorialT;

        public string NodeId { get; private set; }
        public DialogueData.Node Node => NodeId != null && _data.Nodes.TryGetValue(NodeId, out var n) ? n : null;
        public IReadOnlyList<DialogueData.Choice> Choices => (IReadOnlyList<DialogueData.Choice>)Node?.Choices ?? Array.Empty<DialogueData.Choice>();
        public bool Open => NodeId != null;
        /// <summary>fetch_document while the errand is running, return_document after it, null otherwise.</summary>
        public string Quest { get; private set; }
        public string LastRefusal { get; private set; }
        public event Action<string> NodeOpened;

        public DialogueRunner(DialogueData data, Scenario scenario, GameFlow flow, Inventory inventory, ZoneGraph graph,
                              IPlayerRig rig, RuntimeConfig.UiSettings ui)
        {
            _data = data;
            _tutorial = scenario.Tutorial;
            _flow = flow;
            _inventory = inventory;
            _graph = graph;
            _rig = rig;
            _lineS = ui.LineS;
            flow.StateChanged += (from, to) =>
            {
                if (to == FlowState.Tutorial) StartTutorial();
                else if (from == FlowState.Tutorial || to == FlowState.Aborted) { Close(); Quest = null; }
            };
            flow.RunStarted += _ => { Close(); Quest = null; };
        }

        private void StartTutorial()
        {
            _tutorialT = 0;
            Quest = null;
            Show(_tutorial?.StartNode ?? _data.Start);
        }

        public void Show(string nodeId)
        {
            if (nodeId == null || !_data.Nodes.ContainsKey(nodeId)) { Close(); return; }
            NodeId = nodeId;
            _lineLeft = Node.Choices == null || Node.Choices.Count == 0 ? _lineS : 0f;
            NodeOpened?.Invoke(nodeId);
        }

        public void Close() => NodeId = null;

        /// <summary>Pick a choice by its intent (play scripts, number keys, clicks).</summary>
        public void Choose(string intent)
        {
            var node = Node;
            if (node == null) { LastRefusal = "no dialogue open"; return; }
            var choice = node.Choices?.FirstOrDefault(c => c.Intent == intent);
            if (choice == null) { LastRefusal = $"'{intent}' is not a choice of {NodeId}"; return; }
            Advance(node, choice.Next);
        }

        public void ChooseIndex(int index)
        {
            if (index >= 0 && index < Choices.Count) Choose(Choices[index].Intent);
            else if (Open && Choices.Count == 0) Continue();
        }

        /// <summary>A line without choices: go on now.</summary>
        public void Continue()
        {
            var node = Node;
            if (node != null && (node.Choices == null || node.Choices.Count == 0)) Advance(node, node.Next);
        }

        private void Advance(DialogueData.Node node, string next)
        {
            if (next != null) { Show(next); return; }
            Close();
            OnEnd(node.OnEnd);
        }

        private void OnEnd(DialogueData.OnEnd end)
        {
            if (end == null) return;
            if (end.Quest != null) Quest = end.Quest;
            if (end.Flow == "to_prefire") _flow.TutorialDone();
        }

        /// <summary>Every frame (scaled delta time).</summary>
        public void Update(float dt)
        {
            if (Open && _lineLeft > 0f)
            {
                _lineLeft -= dt;
                if (_lineLeft <= 0f) Continue();
            }
            if (_flow.State != FlowState.Tutorial || _flow.Paused) return;
            _tutorialT += dt;
            if (_tutorial != null && _tutorialT >= _tutorial.MaxS)
            {
                Debug.Log("[FireDrill] tutorial time limit reached");
                Close();
                _flow.TutorialDone();
                return;
            }
            if (Quest == "fetch_document" && !Open && _tutorial != null && _inventory.Has(_tutorial.ErrandObject)
                && _graph.ZoneAt(_rig.Head) == _tutorial.ReturnZone)
            {
                Quest = "returning";
                Show(_tutorial.ReturnNode);
            }
        }
    }
}
