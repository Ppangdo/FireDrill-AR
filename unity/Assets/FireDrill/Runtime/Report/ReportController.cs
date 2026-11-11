using System;
using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>What the report screen shows for one run.</summary>
    public sealed class ReportModel
    {
        public string RunId;
        public int Attempt;
        public EvaluationResult Evaluation;
        /// <summary>Sentences on screen: the template at once, replaced when the server's report arrives.</summary>
        public ReportText Text;
        /// <summary>"template" (local), or the server's model ("fake", "claude-sonnet-5-5", or "template" when the server fell back).</summary>
        public string Source = "template";
        public bool ServerFallback;
        public bool Pending;
        public float ShownAtRealtime;
        public float? TextArrivedAfterS;
        public string Error;
        /// <summary>Same session, previous attempt (for "did the evacuation start sooner", 16.13).</summary>
        public EvaluationResult Previous;
        /// <summary>Run t of ignition (orange marker on the timeline), null if the fire never started.</summary>
        public float? FireT;
        /// <summary>16.9 NPC results (name, result) in scenario order.</summary>
        public List<(string name, NpcResult result)> NpcResults = new List<(string, NpcResult)>();
    }

    /// <summary>명세서 8장 report flow: score and table immediately with the evaluation.json template, then the LLM
    /// sentences from the server if they arrive within report_timeout_s and cover exactly the same items. The LLM never
    /// changes scores: only Text is replaced.</summary>
    public sealed class ReportController
    {
        private readonly EvaluationConfig _cfg;
        private readonly ServerClient _server;
        private readonly List<ReportModel> _history = new List<ReportModel>();

        public ReportModel Current { get; private set; }
        public IReadOnlyList<ReportModel> History => _history;
        public event Action<ReportModel> Changed;

        public ReportController(EvaluationConfig cfg, ServerClient server)
        {
            _cfg = cfg;
            _server = server;
        }

        public void Start(string runId, int attempt, EvaluationResult evaluation, float? fireT)
        {
            var model = new ReportModel
            {
                RunId = runId,
                Attempt = attempt,
                Evaluation = evaluation,
                Text = Evaluator.TemplateReport(evaluation, _cfg),
                Pending = true,
                ShownAtRealtime = Time.realtimeSinceStartup,
                Previous = _history.LastOrDefault()?.Evaluation,
                FireT = fireT,
            };
            Current = model;
            _history.Add(model);
            Changed?.Invoke(model);

            _server.RequestReport(runId, evaluation, (res, err) =>
            {
                model.Pending = false;
                if (res?.Report != null && SameItems(res.Report, model.Text))
                {
                    model.Text = res.Report;
                    model.Source = res.Model;
                    model.ServerFallback = res.FallbackUsed;
                    model.TextArrivedAfterS = Time.realtimeSinceStartup - model.ShownAtRealtime;
                }
                else model.Error = res != null ? "report items differ from the evaluation; kept the template" : err;
                if (model.Error != null) Debug.Log("[FireDrill] report: template kept (" + model.Error + ")");
                Changed?.Invoke(model);
            });
        }

        /// <summary>The server already checks this; the client checks again so wrong sentences never reach the screen.</summary>
        public static bool SameItems(ReportText a, ReportText b)
        {
            var x = a.Items.Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
            var y = b.Items.Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
            return x.SequenceEqual(y) && !string.IsNullOrWhiteSpace(a.Summary);
        }
    }
}
