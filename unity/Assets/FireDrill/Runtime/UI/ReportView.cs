using System;
using System.Linq;
using FireDrill.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireDrill.Runtime
{
    /// <summary>16.11 report screen (UI Toolkit; layout ReportView.uxml, style Report.uss under Resources/FireDrillUI).
    /// Shows a ReportModel: result and score, summary, timeline, item table, NPC results, retry / quit.
    /// Scores and the table appear at once; the sentences are replaced when the server's report arrives.</summary>
    public sealed class ReportView : MonoBehaviour
    {
        private ConfigSet _config;
        private UIDocument _doc;
        private PanelSettings _panel;
        private VisualElement _root;
        private Label _outcome, _score, _meta, _summary, _source, _compareDelay, _compareScore, _npc;
        private VisualElement _comparison;
        private TimelineGraph _timeline;
        private ScrollView _items;
        private ReportModel _model;

        public bool Visible => _root != null && _root.style.display.value != DisplayStyle.None;
        public ReportModel Model => _model;
        public event Action RetryClicked;
        public event Action QuitClicked;

        /// <summary>Labels as rendered, for tests: summary text and per-item lines.</summary>
        public string SummaryText => _summary?.text;
        public string SourceText => _source?.text;
        public int ItemRows => _items?.contentContainer.childCount ?? 0;

        public void Init(ConfigSet config)
        {
            _config = config;
            var ui = config.Runtime.Ui;
            _doc = UiPanel.Create(gameObject, ui, "ReportView", 10, out _panel);
            _root = _doc.rootVisualElement.Q("report-root");
            _outcome = _root.Q<Label>("outcome");
            _score = _root.Q<Label>("score");
            _meta = _root.Q<Label>("meta");
            _summary = _root.Q<Label>("summary");
            _source = _root.Q<Label>("source");
            _comparison = _root.Q("comparison");
            _compareDelay = _comparison.Q<Label>("compare-delay");
            _compareScore = _comparison.Q<Label>("compare-score");
            _npc = _root.Q<Label>("npc");
            _items = _root.Q<ScrollView>("items");
            _timeline = new TimelineGraph();
            _root.Q("timeline").Add(_timeline);
            _root.Q<Button>("retry").clicked += () => RetryClicked?.Invoke();
            _root.Q<Button>("quit").clicked += () => QuitClicked?.Invoke();
            Hide();
        }

        /// <summary>Render into a texture instead of the screen (tests and screenshots).</summary>
        public void SetTargetTexture(RenderTexture rt) => _panel.targetTexture = rt;

        public void Show(ReportModel model)
        {
            _model = model;
            Refresh();
            _root.style.display = DisplayStyle.Flex;
        }

        public void Hide()
        {
            if (_root != null) _root.style.display = DisplayStyle.None;
        }

        /// <summary>The model changed (server sentences arrived): redraw if it is the one on screen.</summary>
        public void OnModelChanged(ReportModel model)
        {
            if (model == _model) Refresh();
        }

        private void Refresh()
        {
            var m = _model;
            if (m == null) return;
            var ev = m.Evaluation;
            var ui = _config.Runtime.Ui;

            _outcome.text = Evaluator.OutcomeName(ev.Outcome);
            foreach (var o in Enum.GetNames(typeof(Outcome))) _outcome.RemoveFromClassList(o.ToLowerInvariant());
            _outcome.AddToClassList(ev.Outcome.ToString().ToLowerInvariant());
            _score.text = $"총점 {ev.TotalPct}%";
            _meta.text = $"{m.Attempt}번째 시도 · 걸린 시간 {ev.ElapsedS:0}초";
            _summary.text = m.Text.Summary;
            _source.text = m.Pending ? "문장을 다듬는 중… (지금은 기본 문장)"
                : m.Source == "template" || m.ServerFallback ? "기본 문장(평가 기준표)으로 작성"
                : $"AI가 다듬은 문장 ({m.Source}) · 점수와 등급은 규칙으로 계산";
            RefreshComparison(m);

            _timeline.SetData(ev.Timeline, ui.TimelineMaxC, _config.Graph, m.FireT);

            _items.Clear();
            var texts = m.Text.Items.ToDictionary(i => i.Id);
            foreach (var item in ev.Items)
            {
                var cfg = _config.Evaluation.Items.FirstOrDefault(i => i.Id == item.Id);
                if (cfg == null || (!cfg.Mvp && item.Grade == Grade.Na)) continue; // non-MVP items that did not apply
                var grade = item.Grade.ToString().ToLowerInvariant();
                var row = new VisualElement();
                row.AddToClassList("item");
                var name = new Label(cfg.Name);
                name.AddToClassList("item-name");
                var gradeLabel = new Label(ui.Grades.TryGetValue(grade, out var g) ? g : grade);
                gradeLabel.AddToClassList("grade");
                gradeLabel.AddToClassList(grade);
                var col = new VisualElement();
                col.AddToClassList("item-text");
                if (texts.TryGetValue(item.Id, out var t))
                {
                    var line = !string.IsNullOrEmpty(t.Good) ? t.Good : t.Improve;
                    if (!string.IsNullOrEmpty(line)) col.Add(Line(line, "item-line"));
                    if (!string.IsNullOrEmpty(t.Guideline)) col.Add(Line("행동요령: " + t.Guideline, "item-guideline"));
                }
                row.Add(name);
                row.Add(gradeLabel);
                row.Add(col);
                _items.Add(row);
            }

            var labels = ui.NpcResults;
            _npc.text = m.NpcResults.Count == 0 ? "" :
                "NPC 결과: " + string.Join(", ", m.NpcResults.Select(r =>
                    $"{r.name} {(labels.TryGetValue(r.result.ToString().ToLowerInvariant(), out var l) ? l : r.result.ToString())}"));
        }

        private static Label Line(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            return l;
        }

        /// <summary>16.13: does the second attempt start the evacuation sooner? Uses evac_delay evidence.</summary>
        private void RefreshComparison(ReportModel m)
        {
            UiPanel.Show(_comparison, m.Previous != null);
            if (m.Previous == null) return;
            float? Delay(EvaluationResult e)
            {
                var item = e.Items.FirstOrDefault(i => i.Id == "evac_delay");
                var v = item?.Evidence?["delay_s"];
                return v == null || v.Type == Newtonsoft.Json.Linq.JTokenType.Null ? (float?)null : (float)v;
            }
            var before = Delay(m.Previous);
            var now = Delay(m.Evaluation);
            string FormatDelay(float? seconds) => seconds.HasValue ? $"{seconds.Value:0.#}초" : "측정 안 됨";
            _compareDelay.text = $"대피 개시까지  {FormatDelay(before)} → {FormatDelay(now)}";
            _compareScore.text = $"총점  {m.Previous.TotalPct}% → {m.Evaluation.TotalPct}%";
        }

        private void OnDestroy()
        {
            if (_panel != null) Destroy(_panel);
        }
    }
}
