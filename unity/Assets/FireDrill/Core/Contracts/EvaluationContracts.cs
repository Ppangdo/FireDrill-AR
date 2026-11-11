using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace FireDrill.Core
{
    public enum Grade { Good, Normal, Poor, Na }

    /// <summary>evaluation.json (contracts/schemas/evaluation.schema.json).</summary>
    public sealed class EvaluationConfig
    {
        public int SchemaVersion { get; set; }
        public bool MvpOnly { get; set; }
        public Dictionary<string, float> GradeRatio { get; set; }
        public float StandingHeadM { get; set; }
        public float PostureThreshold { get; set; }
        public string SummaryTemplate { get; set; }
        public List<Item> Items { get; set; }
        public Dictionary<string, Rule> Rules { get; set; }

        public sealed class Item
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public bool Mvp { get; set; }
            public float Weight { get; set; }
            public Dictionary<string, float> Thresholds { get; set; }
            public string RuleRef { get; set; }
            /// <summary>The exact algorithm. Reference: contracts/tools/ref_eval.py.</summary>
            public string Measure { get; set; }
            public ItemText Text { get; set; }
        }

        public sealed class ItemText { public string Good { get; set; } public string Improve { get; set; } public string Guideline { get; set; } }
        public sealed class Rule { public string Source { get; set; } public string Quote { get; set; } public string CrossRef { get; set; } }
    }

    /// <summary>Evaluator output = LLM report input (contracts/schemas/evaluation_result.schema.json).</summary>
    public sealed class EvaluationResult
    {
        public int SchemaVersion { get; set; } = FireDrillJson.SchemaVersion;
        public string RunId { get; set; }
        public Outcome Outcome { get; set; }
        public int TotalPct { get; set; }
        public float ElapsedS { get; set; }
        public List<ItemResult> Items { get; set; } = new List<ItemResult>();
        public List<TimelinePoint> Timeline { get; set; } = new List<TimelinePoint>();
        public List<string> WeakZones { get; set; } = new List<string>();
    }

    public sealed class ItemResult
    {
        public string Id { get; set; }
        public Grade Grade { get; set; }
        public float Points { get; set; }
        public float Max { get; set; }
        public JObject Evidence { get; set; } = new JObject();
        public string RuleRef { get; set; }
    }

    public sealed class TimelinePoint
    {
        public float T { get; set; }
        public float CHead { get; set; }
        public string Zone { get; set; }
    }

    public sealed class ReportText
    {
        public string Summary { get; set; }
        public List<ReportItemText> Items { get; set; } = new List<ReportItemText>();
    }

    public sealed class ReportItemText
    {
        public string Id { get; set; }
        public string Good { get; set; }
        public string Improve { get; set; }
        public string Guideline { get; set; }
    }

    /// <summary>
    /// Pure rule-based scoring (명세서 7장). Same log → same result. Grades must match
    /// contracts/tools/ref_eval.py and contracts/golden/*.expected.json. Stage 1 implements.
    /// total_pct = floor(100 × Σpoints / Σmax over non-na items + 0.5), 0 if every item is na.
    /// </summary>
    public static class Evaluator
    {
        public static EvaluationResult Evaluate(IReadOnlyList<LogEvent> events, ZoneGraph graph, Scenario scenario, EvaluationConfig cfg)
            => throw new NotImplementedException("Stage 1");

        /// <summary>Offline report from evaluation.json text templates (명세서 16.12).</summary>
        public static ReportText TemplateReport(EvaluationResult result, EvaluationConfig cfg)
            => throw new NotImplementedException("Stage 1");
    }
}
