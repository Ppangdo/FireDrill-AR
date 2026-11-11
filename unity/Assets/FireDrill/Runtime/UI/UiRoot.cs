using System.Collections.Generic;
using System.Linq;
using System.Text;
using FireDrill.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireDrill.Runtime
{
    /// <summary>명세서 16.11 screens except the report (ReportView): start briefing, HUD (subtitle, interaction prompt,
    /// inventory, pause banner), dialogue box (tutorial and NPC), ending and aborted. Presentation only: it reads
    /// GameFlow and the modules each frame and sends button presses back to them.</summary>
    public sealed class UiRoot : MonoBehaviour
    {
        private Bootstrap _boot;
        private RuntimeConfig.UiSettings _ui;
        private UIDocument _doc;
        private PanelSettings _panel;
        private VisualElement _hud, _dialogue, _start, _ending, _aborted, _choices;
        private Label _paused, _prompt, _subtitle, _inventory, _speaker, _line, _server, _endingTitle, _endingDetail;
        private TextField _code;
        private string _shownNode;

        public bool StartShown => UiPanel.Shown(_start);
        public bool HudShown => UiPanel.Shown(_hud);
        public bool DialogueShown => UiPanel.Shown(_dialogue);
        public bool EndingShown => UiPanel.Shown(_ending);
        public bool AbortedShown => UiPanel.Shown(_aborted);
        public string DialogueText => _line?.text;
        public string SubtitleText => _subtitle?.text;
        public string EndingTitle => _endingTitle?.text;
        public string InventoryText => _inventory?.text;
        /// <summary>Dialogue with choices is open: the platform frees the cursor so the choices can be clicked.</summary>
        public bool WantsCursor { get; private set; }

        public void Init(Bootstrap boot)
        {
            _boot = boot;
            _ui = boot.Config.Runtime.Ui;
            _doc = UiPanel.Create(gameObject, _ui, "UiRoot", "Hud", 5, out _panel); // uss name differs: Resources.Load picks one asset per name
            var r = _doc.rootVisualElement;
            _hud = r.Q("hud");
            _dialogue = r.Q("dialogue");
            _start = r.Q("start");
            _ending = r.Q("ending");
            _aborted = r.Q("aborted");
            _choices = r.Q("choices");
            _paused = r.Q<Label>("paused");
            _prompt = r.Q<Label>("prompt");
            _subtitle = r.Q<Label>("subtitle");
            _inventory = r.Q<Label>("inventory");
            _speaker = r.Q<Label>("speaker");
            _line = r.Q<Label>("line");
            _server = r.Q<Label>("server");
            _endingTitle = r.Q<Label>("ending-title");
            _endingDetail = r.Q<Label>("ending-detail");
            _code = r.Q<TextField>("code");
            _code.value = "";
            r.Q<Button>("begin").clicked += Begin;
            r.Q<Button>("quit").clicked += Bootstrap.Quit;
            foreach (var e in new[] { _hud, _dialogue, _start, _ending, _aborted }) UiPanel.Show(e, false);
        }

        public void SetTargetTexture(RenderTexture rt) => _panel.targetTexture = rt;

        private void Begin()
        {
            var code = _code.value?.Trim();
            if (string.IsNullOrEmpty(code)) { _code.Focus(); return; }
            _boot.Begin(code);
        }

        private void Update()
        {
            if (_boot?.Flow == null) return;
            var flow = _boot.Flow;
            var state = flow.State;

            UiPanel.Show(_start, state == FlowState.Boot || (state == FlowState.Calibration && _boot.WaitingForStart));
            if (StartShown) _server.text = ServerText();

            bool playing = state == FlowState.Tutorial || state == FlowState.PreFire || state == FlowState.Fire;
            UiPanel.Show(_hud, playing);
            UiPanel.Show(_paused, playing && flow.Paused);
            if (playing)
            {
                _subtitle.text = _boot.Subtitle ?? "";
                _prompt.text = Prompt(_boot.Platform.Input.FocusedTargetId);
                _inventory.text = InventoryLines();
            }

            UpdateDialogue(playing);

            UiPanel.Show(_ending, state == FlowState.Ending);
            if (state == FlowState.Ending) FillEnding();
            UiPanel.Show(_aborted, state == FlowState.Aborted);
        }

        // ---- dialogue ------------------------------------------------------------

        private void UpdateDialogue(bool playing)
        {
            var npcs = _boot.Npcs;
            var runner = _boot.Dialogue;
            DialogueData.Node node = null;
            string key = null;
            IReadOnlyList<DialogueData.Choice> choices = System.Array.Empty<DialogueData.Choice>();
            if (playing && npcs.Talking != null && npcs.Node != null) { node = npcs.Node; key = "npc:" + npcs.Talking; choices = npcs.Choices; }
            else if (playing && runner.Open) { node = runner.Node; key = runner.NodeId; choices = runner.Choices; }

            UiPanel.Show(_dialogue, node != null);
            WantsCursor = node != null && choices.Count > 0;
            if (node == null) { _shownNode = null; return; }
            if (key == _shownNode) return;
            _shownNode = key;
            _speaker.text = node.Speaker;
            _line.text = node.Text;
            _choices.Clear();
            for (int i = 0; i < choices.Count && i < 3; i++)
            {
                int index = i;
                var b = new Button(() => Choose(index)) { text = $"{i + 1}. {choices[i].Text}" };
                b.AddToClassList("choice");
                _choices.Add(b);
            }
        }

        private void Choose(int index)
        {
            if (_boot.Npcs.Talking != null) _boot.Npcs.ChooseIndex(index);
            else _boot.Dialogue.ChooseIndex(index);
        }

        // ---- HUD -----------------------------------------------------------------

        private string Prompt(string target)
        {
            if (target == null) return "";
            var p = _ui.Prompts;
            if (target == ItemInteractions.FireTarget) return _boot.Items.Inventory.HandKind == "extinguisher" ? p["fire"] : "";
            if (_boot.Npcs.IsNpc(target)) return p["talk"];
            if (_boot.Doors.IsDoor(target)) return p["door"];
            var kind = _boot.Items.KindOf(target);
            if (kind == null) return "";
            if (kind == "faucet") return p["faucet"];
            return _boot.Items.IsPickable(target) ? p["pickup"] : p["use"];
        }

        private string InventoryLines()
        {
            var inv = _boot.Items.Inventory;
            string Name(string id) => id == null ? "-" : _ui.Items.TryGetValue(_boot.Items.KindOf(id) ?? "", out var n) ? n : id;
            var bag = inv.Bag;
            var towel = !inv.HasTowel ? _ui.TowelStates["none"]
                : (inv.TowelWet ? _ui.TowelStates["wet"] : _ui.TowelStates["dry"]) + (inv.TowelEquipped ? " · " + _ui.TowelStates["on"] : "");
            var sb = new StringBuilder();
            sb.AppendLine("손: " + Name(inv.Hand));
            sb.AppendLine("가방 1: " + Name(bag.Count > 0 ? bag[0] : null));
            sb.AppendLine("가방 2: " + Name(bag.Count > 1 ? bag[1] : null));
            sb.Append(towel);
            return sb.ToString();
        }

        private string ServerText()
        {
            var s = _boot.Server;
            switch (s.Mode)
            {
                case ServerMode.Online: return "서버 연결됨" + (s.Llm != null ? $" (LLM {s.Llm})" : "");
                case ServerMode.Connecting: return "서버 연결 확인 중…";
                case ServerMode.Disabled: return "오프라인 모드 (서버 없이 진행)";
                default: return "서버에 연결할 수 없어 오프라인으로 진행합니다";
            }
        }

        // ---- ending --------------------------------------------------------------

        private void FillEnding()
        {
            var flow = _boot.Flow;
            var outcome = flow.LastOutcome ?? Outcome.Aborted;
            _endingTitle.text = _ui.Endings.TryGetValue(outcome.ToString().ToLowerInvariant(), out var t) ? t : Evaluator.OutcomeName(outcome);
            float sinceFire = flow.FireT.HasValue ? flow.T - flow.FireT.Value : flow.T;
            _endingDetail.text = $"불이 난 뒤 {sinceFire:0}초 · 마지막 위치의 연기 농도 {_boot.Exposure.CHead * 100f:0}%";
        }

        private void OnDestroy()
        {
            if (_panel != null) Destroy(_panel);
        }
    }
}
