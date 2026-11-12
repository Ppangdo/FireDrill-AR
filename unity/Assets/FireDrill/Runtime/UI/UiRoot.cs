using System.Collections.Generic;
using System.Linq;
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
        private VisualElement _hud, _reticle, _inventory, _dialogue, _start, _ending, _aborted, _choices;
        private Label _paused, _objective, _prompt, _subtitle, _hand, _bag1, _bag2, _itemHelp, _towelHelp, _speaker, _line, _server, _endingTitle, _endingDetail;
        private TextField _code;
        private Button _begin;
        private string _shownNode;

        public bool StartShown => UiPanel.Shown(_start);
        public bool HudShown => UiPanel.Shown(_hud);
        public bool DialogueShown => UiPanel.Shown(_dialogue);
        public bool EndingShown => UiPanel.Shown(_ending);
        public bool AbortedShown => UiPanel.Shown(_aborted);
        public string DialogueText => _line?.text;
        public string SubtitleText => _subtitle?.text;
        public string EndingTitle => _endingTitle?.text;
        /// <summary>Dialogue with choices is open: the platform frees the cursor so the choices can be clicked.</summary>
        public bool WantsCursor { get; private set; }

        public void Init(Bootstrap boot)
        {
            _boot = boot;
            _ui = boot.Config.Runtime.Ui;
            _doc = UiPanel.Create(gameObject, _ui, "UiRoot", 5, out _panel);
            var r = _doc.rootVisualElement;
            _hud = r.Q("hud");
            _reticle = r.Q("reticle");
            _inventory = r.Q("inventory");
            _dialogue = r.Q("dialogue");
            _start = r.Q("start");
            _ending = r.Q("ending");
            _aborted = r.Q("aborted");
            _choices = r.Q("choices");
            _paused = r.Q<Label>("paused");
            _objective = r.Q<Label>("objective");
            _prompt = r.Q<Label>("prompt");
            _subtitle = r.Q<Label>("subtitle");
            _hand = r.Q<Label>("hand-item");
            _bag1 = r.Q<Label>("bag1-item");
            _bag2 = r.Q<Label>("bag2-item");
            _itemHelp = r.Q<Label>("item-help");
            _towelHelp = r.Q<Label>("towel-help");
            _speaker = r.Q<Label>("speaker");
            _line = r.Q<Label>("line");
            _server = r.Q<Label>("server");
            _endingTitle = r.Q<Label>("ending-title");
            _endingDetail = r.Q<Label>("ending-detail");
            _code = r.Q<TextField>("code");
            _code.value = "";
            _code.textEdition.placeholder = "예: T01";
            _begin = r.Q<Button>("begin");
            _begin.clicked += Begin;
            _code.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                if (_begin.enabledSelf) Begin();
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);
            r.Q<Button>("quit").clicked += Bootstrap.Quit;
            foreach (var e in new[] { _hud, _dialogue, _start, _ending, _aborted }) UiPanel.Show(e, false);
        }

        public void SetTargetTexture(RenderTexture rt) => _panel.targetTexture = rt;

        private void Begin()
        {
            var code = _code.value?.Trim();
            if (string.IsNullOrEmpty(code)) { _code.Focus(); return; }
            _begin.SetEnabled(false);
            _boot.Begin(code);
        }

        private void Update()
        {
            if (_boot?.Flow == null) return;
            var flow = _boot.Flow;
            var state = flow.State;

            bool wasShowingStart = StartShown;
            UiPanel.Show(_start, state == FlowState.Boot || state == FlowState.Calibration);
            if (StartShown)
            {
                _server.text = ServerText();
                _begin.SetEnabled(_boot.WaitingForStart);
                _begin.text = state == FlowState.Calibration && !_boot.WaitingForStart
                    ? "훈련 준비 중…" : "요원 확인 · 훈련 시작";
                if (!wasShowingStart) _code.Focus();
            }

            bool playing = state == FlowState.Tutorial || state == FlowState.PreFire || state == FlowState.Fire;
            UiPanel.Show(_hud, playing);
            UiPanel.Show(_paused, playing && flow.Paused);
            if (playing)
            {
                _subtitle.text = _boot.Subtitle ?? "";
                _objective.text = Objective(state);
                UiPanel.Show(_objective, _objective.text.Length > 0);
                _prompt.text = Prompt(_boot.Platform.Input.FocusedTargetId);
                _reticle.EnableInClassList("target-ready", _prompt.text.Length > 0);
                UpdateInventory();
            }

            UpdateDialogue(playing);
            _inventory.EnableInClassList("above-dialogue", DialogueShown);
            UiPanel.Show(_reticle, playing && !WantsCursor && !flow.Paused);

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

        private string Objective(FlowState state)
        {
            var errand = _boot.Config.Scenario.Tutorial?.ErrandObject;
            if (errand == null) return "";
            bool hasDocument = _boot.Items.Inventory.Has(errand);
            if (state == FlowState.PreFire && hasDocument)
                return "서류 전달 완료 · 실습실에서 잠시 대기하세요.";
            if (state != FlowState.Tutorial) return "";
            if (_boot.Dialogue.Quest == "returning") return "교수님께 서류 전달 · 대화에서 1번을 선택하세요.";
            if (_boot.Dialogue.Quest != "fetch_document") return "";
            return hasDocument
                ? "서류를 들고 302호 실습실로 돌아가세요."
                : "301호 강의실 교탁 위 서류를 가져오세요. 가까이서 E를 누르면 줍습니다.";
        }

        private string Prompt(string target)
        {
            if (_boot.Items.Busy) return "물건을 주웠습니다";
            if (_boot.Items.IsSpraying)
            {
                var phase = _boot.Fire.Engine.Phase;
                if (phase == FirePhase.Extinguished) return "진화 완료 · 남은 연기를 피해 대피하세요";
                if (phase == FirePhase.Growing || phase == FirePhase.Full) return "불이 커졌습니다 · 분사를 멈추고 대피하세요";
                if (!_boot.Items.SprayHitsFire) return "분사 중 · 불을 향해 조준하세요";
                return $"초기 화재 진화 중 · {_boot.Items.ExtinguishProgress:P0}";
            }
            if (target == null) return "";
            var p = _ui.Prompts;
            if (target == ItemInteractions.FireTarget) return _boot.Items.Inventory.HandKind == "extinguisher" ? p["fire"] : "";
            if (_boot.Npcs.IsNpc(target)) return p["talk"];
            if (_boot.Doors.IsDoor(target)) return p["door"];
            var kind = _boot.Items.KindOf(target);
            if (kind == null) return "";
            if (kind == "faucet")
            {
                var inventory = _boot.Items.Inventory;
                if (!inventory.HasTowel) return "수건을 먼저 주우세요";
                if (inventory.TowelWet) return "수건이 젖었습니다";
                if (_boot.Platform.Input is IHeldUseFeedback held && held.HeldUseSeconds > 0f)
                {
                    var duration = _boot.Config.Runtime.Pc.FaucetHoldS;
                    var percent = Mathf.FloorToInt(Mathf.Clamp01(held.HeldUseSeconds / duration) * 100f);
                    return $"E 계속 누르기 · 수건 적시는 중 {percent}%";
                }
                return p["faucet"];
            }
            if (kind == "alarm") return "E: 화재경보 발신기 누르기";
            return _boot.Items.IsPickable(target) && _ui.Items.TryGetValue(kind, out var name)
                ? p["pickup"] + " · " + name
                : p["use"];
        }

        private void UpdateInventory()
        {
            var inv = _boot.Items.Inventory;
            string Name(string id) => id == null ? "비어 있음" :
                _ui.Items.TryGetValue(_boot.Items.KindOf(id) ?? "", out var name) ? name : id;

            _hand.text = Name(inv.Hand);
            _bag1.text = Name(inv.Bag.Count > 0 ? inv.Bag[0] : null);
            _bag2.text = Name(inv.Bag.Count > 1 ? inv.Bag[1] : null);
            _itemHelp.text = inv.HandKind switch
            {
                "extinguisher" => $"마우스 왼쪽 길게 · 분사 ({_boot.Items.ExtinguisherLeftS:0.#}초 남음)    G · 내려놓기",
                "towel" => "G · 손에 든 수건 내려놓기",
                null => "Z / X · 가방에서 꺼내기",
                _ => "G · 손에 든 물건 내려놓기",
            };
            if (inv.HandKind != "extinguisher" && inv.FindKind("extinguisher") != null)
            {
                string key = inv.Bag.Count > 0 && _boot.Items.KindOf(inv.Bag[0]) == "extinguisher" ? "Z" : "X";
                _itemHelp.text = key + " · 소화기 꺼내기 → 마우스 왼쪽 길게 · 분사";
            }

            _towelHelp.text = inv.HasTowel
                ? (inv.TowelWet ? _ui.TowelStates["wet"] : _ui.TowelStates["dry"]) + " · " +
                    (inv.TowelEquipped ? "T · 코·입에서 떼기" : "T · 코·입에 대기") +
                    (inv.TowelWet ? "" : "    수도에서 E 길게 · 적시기")
                : "";
            UiPanel.Show(_towelHelp, inv.HasTowel);
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
