using System.Collections.Generic;
using System.IO;
using System.Linq;
using FireDrill.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FireDrill.Tests
{
    /// <summary>
    /// 11장 완료 기준 — 평가 엔진: the three golden logs give exactly contracts/golden/*.expected.json
    /// (outcome, all 10 grades, total_pct), the same as contracts/tools/ref_eval.py.
    /// </summary>
    public class EvaluatorTests
    {
        static EvaluationResult Evaluate(IReadOnlyList<LogEvent> log) =>
            Evaluator.Evaluate(log, CoreTestData.Graph(), CoreTestData.Scenario(), CoreTestData.Evaluation());

        static JObject Expected(string name) =>
            JObject.Parse(File.ReadAllText(Path.Combine(CoreTestData.GoldenDir, name + ".expected.json")));

        static string Snake(Grade g) => g == Grade.Na ? "na" : g.ToString().ToLowerInvariant();

        [TestCase("model_run")]
        [TestCase("fail_run")]
        [TestCase("na_run")]
        public void GoldenLog_MatchesExpected(string name)
        {
            var exp = Expected(name);
            var r = Evaluate(CoreTestData.GoldenLog(name));

            var grades = (JObject)exp["grades"];
            Assert.AreEqual(grades.Count, r.Items.Count);
            foreach (var item in r.Items)
                Assert.AreEqual((string)grades[item.Id], Snake(item.Grade), $"{name}: {item.Id} evidence {item.Evidence.ToString(Newtonsoft.Json.Formatting.None)}");
            Assert.AreEqual((string)exp["outcome"], FireDrillJson.Serialize(r.Outcome).Trim('"'), name);
            Assert.AreEqual((int)exp["total_pct"], r.TotalPct, name);
        }

        [TestCase("model_run")]
        [TestCase("fail_run")]
        [TestCase("na_run")]
        public void Result_IsConsistent(string name)
        {
            var log = CoreTestData.GoldenLog(name);
            var cfg = CoreTestData.Evaluation();
            var r = Evaluate(log);

            Assert.AreEqual(log[0].RunId, r.RunId);
            Assert.AreEqual(log.Last().T, r.ElapsedS);
            Assert.AreEqual(log.Count(e => e.Type == EventTypes.Sample), r.Timeline.Count);
            Assert.LessOrEqual(r.WeakZones.Count, 2);
            CollectionAssert.AreEqual(cfg.Items.Select(i => i.Id), r.Items.Select(i => i.Id), "evaluation.json order");
            foreach (var item in r.Items)
            {
                var c = cfg.Items.Single(i => i.Id == item.Id);
                Assert.AreEqual(c.RuleRef, item.RuleRef);
                Assert.AreEqual(c.Weight, item.Max);
                Assert.AreEqual(item.Grade == Grade.Na ? 0f : c.Weight * cfg.GradeRatio[Snake(item.Grade)], item.Points, 1e-6);
            }
            var scored = r.Items.Where(i => i.Grade != Grade.Na).ToList();
            Assert.AreEqual((int)System.Math.Floor(100.0 * scored.Sum(i => i.Points) / scored.Sum(i => i.Max) + 0.5), r.TotalPct);

            // Serializes to the contract shape (evaluation_result.schema.json).
            var j = JObject.Parse(FireDrillJson.Serialize(r));
            CollectionAssert.AreEquivalent(
                new[] { "schema_version", "run_id", "outcome", "total_pct", "elapsed_s", "items", "timeline", "weak_zones" },
                j.Properties().Select(p => p.Name));
        }

        [Test]
        public void SameLog_SameResult()
        {
            var log = CoreTestData.GoldenLog("model_run");
            Assert.AreEqual(FireDrillJson.Serialize(Evaluate(log)), FireDrillJson.Serialize(Evaluate(log)));
        }

        [Test]
        public void Evidence_FollowsMeasure()
        {
            var model = Evaluate(CoreTestData.GoldenLog("model_run")).Items.ToDictionary(i => i.Id);
            Assert.AreEqual("wet", (string)model["nose_mouth"].Evidence["towel"]);
            Assert.AreEqual(2, (int)model["warn_others"].Evidence["helped"]);
            Assert.AreEqual(0, (int)model["no_elevator"].Evidence["calls"]);
            Assert.LessOrEqual((double)model["evac_delay"].Evidence["delay_s"], 30);

            var fail = Evaluate(CoreTestData.GoldenLog("fail_run")).Items.ToDictionary(i => i.Id);
            Assert.AreEqual(1, (int)fail["belongings"].Evidence["count"]);
            Assert.Greater((int)fail["no_elevator"].Evidence["calls"], 0);
            Assert.Greater((int)fail["door_check"].Evidence["opened"], (int)fail["door_check"].Evidence["checked"]);
        }

        [Test]
        public void WeakZones_AreSmokyZonesWhereTheTraineeStayed()
        {
            var r = Evaluate(CoreTestData.GoldenLog("fail_run"));
            Assert.IsNotEmpty(r.WeakZones);
            var graph = CoreTestData.Graph();
            foreach (var z in r.WeakZones) Assert.IsFalse(graph.IsExit(z));
            Assert.IsEmpty(Evaluate(CoreTestData.GoldenLog("na_run")).WeakZones, "never in smoke");
        }

        [Test]
        public void NoCue_TimedItemsAreNa_AndMissingRunEndIsAborted()
        {
            var log = CoreTestData.GoldenLog("model_run")
                .Where(e => e.Type != EventTypes.CueExposed && e.Type != EventTypes.RunEnd).ToList();
            var r = Evaluate(log).Items.ToDictionary(i => i.Id);
            foreach (var id in new[] { "alert", "evac_delay", "door_check", "nose_mouth", "low_posture" })
                Assert.AreEqual(Grade.Na, r[id].Grade, id);
            Assert.AreEqual(Outcome.Aborted, Evaluate(log).Outcome);
        }

        [Test]
        public void AllNa_TotalIsZero()
        {
            var cfg = CoreTestData.Evaluation();
            var scenario = CoreTestData.Scenario();
            scenario.Npcs = new List<Scenario.NpcSetup>();
            // Without a cue, a door or smoke every timed item is na; the two count-based items are made non-MVP.
            foreach (var i in cfg.Items)
                if (i.Id == "no_elevator" || i.Id == "belongings") i.Mvp = false;
            Assert.IsTrue(cfg.MvpOnly);
            var log = new EventLog("r");
            log.Append(EventTypes.RunStart, default, new { scenario_id = "s", attempt = 1 });
            var r = Evaluator.Evaluate(log.Events, CoreTestData.Graph(), scenario, cfg);
            Assert.IsTrue(r.Items.All(i => i.Grade == Grade.Na), string.Join(",", r.Items.Select(i => i.Id + "=" + i.Grade)));
            Assert.AreEqual(0, r.TotalPct);
        }

        // ── template report (16.12) ────────────────────────────────────────────

        [TestCase("model_run")]
        [TestCase("fail_run")]
        [TestCase("na_run")]
        public void TemplateReport_FillsEveryPlaceholder(string name)
        {
            var cfg = CoreTestData.Evaluation();
            var r = Evaluate(CoreTestData.GoldenLog(name));
            var report = Evaluator.TemplateReport(r, cfg);

            StringAssert.Contains($"총점 {r.TotalPct}%", report.Summary);
            Assert.AreEqual(r.Items.Count(i => i.Grade != Grade.Na), report.Items.Count);
            foreach (var t in report.Items)
            {
                var g = r.Items.Single(i => i.Id == t.Id).Grade;
                Assert.AreEqual(g == Grade.Good, t.Good.Length > 0, t.Id);
                Assert.AreEqual(g != Grade.Good, t.Improve.Length > 0, t.Id);
                Assert.IsNotEmpty(t.Guideline);
                foreach (var s in new[] { t.Good, t.Improve, t.Guideline, report.Summary })
                    StringAssert.DoesNotMatch(@"\{[a-z_]+\}", s, $"{name} {t.Id}");
            }
        }

        [Test]
        public void TemplateReport_SummaryNamesBestAndWorst()
        {
            var cfg = CoreTestData.Evaluation();
            var model = Evaluator.TemplateReport(Evaluate(CoreTestData.GoldenLog("model_run")), cfg);
            Assert.AreEqual("결과 탈출 성공, 총점 100%. 가장 잘한 점: 대피 개시 지연. 가장 먼저 고칠 점: 없음.", model.Summary);

            var fail = Evaluator.TemplateReport(Evaluate(CoreTestData.GoldenLog("fail_run")), cfg);
            Assert.AreEqual("결과 실패, 총점 6%. 가장 잘한 점: 소지품 수집 지연. 가장 먼저 고칠 점: 대피 개시 지연.", fail.Summary);

            var lowPosture = Evaluate(CoreTestData.GoldenLog("model_run")).Items.Single(i => i.Id == "low_posture");
            var text = Evaluator.TemplateReport(Evaluate(CoreTestData.GoldenLog("model_run")), cfg).Items.Single(i => i.Id == "low_posture");
            StringAssert.Contains($"({System.Math.Round((double)lowPosture.Evidence["low_ratio"] * 100)}%)", text.Good);
        }
    }
}
