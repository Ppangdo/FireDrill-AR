using System.Linq;
using FireDrill.Core;
using FireDrill.Runtime;
using NUnit.Framework;

namespace FireDrill.Tests
{
    /// <summary>명세서 8장 report rules on the client: template at once, offline keeps it, item ids must match,
    /// and the previous attempt is kept for the 16.13 comparison.</summary>
    public class ReportControllerTests
    {
        private static EvaluationResult Evaluate(string golden) =>
            Evaluator.Evaluate(CoreTestData.GoldenLog(golden), CoreTestData.Graph(), CoreTestData.Scenario(), CoreTestData.Evaluation());

        private static ServerClient OfflineServer()
        {
            var app = CoreTestData.Config<AppConfig>("app_config.json");
            app.Offline = true;
            var rt = CoreTestData.Config<RuntimeConfig>("runtime.json");
            return new ServerClient(app, rt.Network, rt.Flow.RunCreateWaitS);
        }

        [Test]
        public void Offline_ShowsTemplateImmediately_AndKeepsIt()
        {
            using var server = OfflineServer();
            var c = new ReportController(CoreTestData.Evaluation(), server);
            int changes = 0;
            c.Changed += _ => changes++;
            var ev = Evaluate("fail_run");
            c.Start("r1", 1, ev, 10f);
            var m = c.Current;
            Assert.AreEqual("template", m.Source);
            Assert.IsFalse(m.Pending, "offline answers at once");
            Assert.AreEqual(Evaluator.TemplateReport(ev, CoreTestData.Evaluation()).Summary, m.Text.Summary);
            Assert.AreEqual("offline", m.Error);
            Assert.AreEqual(2, changes);
            Assert.AreEqual(10f, m.FireT);
        }

        [Test]
        public void SecondAttempt_KnowsThePrevious()
        {
            using var server = OfflineServer();
            var c = new ReportController(CoreTestData.Evaluation(), server);
            c.Start("r1", 1, Evaluate("fail_run"), null);
            Assert.IsNull(c.Current.Previous);
            c.Start("r2", 2, Evaluate("model_run"), null);
            Assert.AreEqual(6, c.Current.Previous.TotalPct);
            Assert.AreEqual(2, c.History.Count);
        }

        [Test]
        public void SameItems_RequiresTheSameIdSetAndASummary()
        {
            var cfg = CoreTestData.Evaluation();
            var t = Evaluator.TemplateReport(Evaluate("model_run"), cfg);
            var copy = new ReportText { Summary = "다른 요약", Items = t.Items.AsEnumerable().Reverse().ToList() };
            Assert.IsTrue(ReportController.SameItems(copy, t), "order does not matter");
            var missing = new ReportText { Summary = "x", Items = t.Items.Skip(1).ToList() };
            Assert.IsFalse(ReportController.SameItems(missing, t));
            var extra = new ReportText { Summary = "x", Items = t.Items.Concat(new[] { new ReportItemText { Id = "extinguish" } }).ToList() };
            Assert.IsFalse(ReportController.SameItems(extra, t));
            Assert.IsFalse(ReportController.SameItems(new ReportText { Summary = " ", Items = t.Items }, t));
        }
    }
}
