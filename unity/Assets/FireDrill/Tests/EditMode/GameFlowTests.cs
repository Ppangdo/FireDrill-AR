using System;
using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using FireDrill.Runtime;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FireDrill.Tests
{
    /// <summary>11장 완료 기준 — GameFlow: emergency stop from every state goes to Aborted at once; a retry is a new run_id.</summary>
    public class GameFlowTests
    {
        private sealed class FakeRecorder : IRunRecorder
        {
            public readonly List<(string type, JObject data)> Events = new List<(string, JObject)>();
            public string RunId;
            public bool Open;
            public void BeginRun(string runId) { RunId = runId; Open = true; }
            public void Record(string type, object data = null)
            {
                Assert.IsTrue(Open, "record outside a run: " + type);
                Events.Add((type, data == null ? new JObject() : JObject.FromObject(data)));
            }
            public void EndRun() => Open = false;
            public IEnumerable<string> Types => Events.Select(e => e.type);
        }

        /// <summary>Answers only when told to (server round trip).</summary>
        private sealed class ManualRunIds : IRunIdSource
        {
            public readonly Queue<Action<string>> Pending = new Queue<Action<string>>();
            public void CreateRun(int attempt, Action<string> done) => Pending.Enqueue(done);
            public void Answer(string id) => Pending.Dequeue()(id);
        }

        private Scenario _scenario;
        private RuntimeConfig _rt;
        private FakeRecorder _rec;

        [SetUp]
        public void SetUp()
        {
            _scenario = CoreTestData.Scenario();
            _rt = CoreTestData.Config<RuntimeConfig>("runtime.json");
            _rec = new FakeRecorder();
        }

        private GameFlow Flow(IRunIdSource ids = null) => new GameFlow(_scenario, _rt.Flow, _rec, ids ?? new LocalRunIds());

        private static void Run(GameFlow f, float seconds, float dt = 0.1f)
        {
            for (float t = 0; t < seconds - 1e-4f; t += dt) f.Tick(dt);
        }

        private GameFlow ToState(FlowState target)
        {
            var f = Flow();
            if (target == FlowState.Boot) return f;
            f.BootDone();
            if (target == FlowState.Calibration) return f;
            f.CalibrationDone();
            if (target == FlowState.Tutorial) return f;
            f.TutorialDone();
            if (target == FlowState.PreFire) return f;
            Run(f, _scenario.Fire.FireAtS + 0.05f);
            if (target == FlowState.Fire) return f;
            f.ReachedExit();
            if (target == FlowState.Ending) return f;
            Run(f, _rt.Flow.EndingS + 0.05f);
            Assert.AreEqual(FlowState.Report, f.State);
            return f;
        }

        [Test]
        public void HappyPath_LogsStatesAndRun()
        {
            var f = ToState(FlowState.Fire);
            Assert.AreEqual(FlowState.Fire, f.State);
            Assert.AreEqual(_scenario.Fire.FireAtS, f.FireT.Value, 0.11f);
            f.ReachedExit();
            Assert.AreEqual(FlowState.Ending, f.State);
            Assert.AreEqual(Outcome.Escaped, f.LastOutcome);
            Assert.IsFalse(_rec.Open);
            CollectionAssert.AreEqual(new[] { "RUN_START", "STATE_CHANGE", "STATE_CHANGE", "STATE_CHANGE", "STATE_CHANGE", "RUN_END" }, _rec.Types.ToArray());
            var changes = _rec.Events.Where(e => e.type == "STATE_CHANGE").Select(e => (string)e.data["from"] + ">" + (string)e.data["to"]).ToArray();
            CollectionAssert.AreEqual(new[] { "Calibration>Tutorial", "Tutorial>PreFire", "PreFire>Fire", "Fire>Ending" }, changes);
            Assert.AreEqual(1, (int)_rec.Events[0].data["attempt"]);
            Assert.AreEqual("escaped", (string)_rec.Events.Last().data["outcome"]);
        }

        [Test]
        public void Retry_IsNewRunAtPreFire_WithRetryFireTime()
        {
            var f = ToState(FlowState.Report);
            var first = f.RunId;
            _rec.Events.Clear();
            f.Retry();
            Assert.AreNotEqual(first, f.RunId);
            Assert.AreEqual(2, f.Attempt);
            Assert.AreEqual(FlowState.PreFire, f.State);
            Assert.AreEqual(0f, f.T);
            Assert.AreEqual("Report>PreFire", (string)_rec.Events[1].data["from"] + ">" + (string)_rec.Events[1].data["to"]);
            Run(f, _scenario.Fire.RetryFireAtS.Value - 0.5f);
            Assert.AreEqual(FlowState.PreFire, f.State);
            Run(f, 0.6f);
            Assert.AreEqual(FlowState.Fire, f.State);
        }

        [TestCase(FlowState.Boot)]
        [TestCase(FlowState.Calibration)]
        [TestCase(FlowState.Tutorial)]
        [TestCase(FlowState.PreFire)]
        [TestCase(FlowState.Fire)]
        [TestCase(FlowState.Ending)]
        [TestCase(FlowState.Report)]
        public void Abort_FromEveryState_IsImmediate(FlowState state)
        {
            var f = ToState(state);
            Assert.AreEqual(state, f.State);
            bool inRun = f.RunActive;
            int before = _rec.Events.Count;
            f.Abort("staff");
            Assert.AreEqual(FlowState.Aborted, f.State, "no tick needed");
            Assert.IsFalse(f.RunActive);
            var added = _rec.Events.Skip(before).ToList();
            if (inRun)
            {
                CollectionAssert.AreEqual(new[] { "ABORT", "STATE_CHANGE", "RUN_END" }, added.Select(e => e.type).ToArray());
                Assert.AreEqual("staff", (string)added[0].data["by"]);
                Assert.AreEqual("Aborted", (string)added[1].data["to"]);
                Assert.AreEqual("aborted", (string)added[2].data["outcome"]);
                Assert.AreEqual(Outcome.Aborted, f.LastOutcome);
            }
            else Assert.IsEmpty(added);
            f.Abort("staff"); // idempotent
            Assert.AreEqual(before + (inRun ? 3 : 0), _rec.Events.Count);
            Run(f, 300f, 1f);
            Assert.AreEqual(FlowState.Aborted, f.State);
        }

        [Test]
        public void Timeout_AfterScenarioLimit()
        {
            var f = ToState(FlowState.Fire);
            Run(f, _scenario.TimeoutS - 1f, 0.5f);
            Assert.AreEqual(FlowState.Fire, f.State);
            Run(f, 1.5f, 0.5f);
            Assert.AreEqual(FlowState.Ending, f.State);
            Assert.AreEqual(Outcome.Timeout, f.LastOutcome);
        }

        [Test]
        public void FedOne_Fails_AndExitBeforeFireIsIgnored()
        {
            var f = ToState(FlowState.PreFire);
            f.ReachedExit();
            f.FedReachedOne();
            Assert.AreEqual(FlowState.PreFire, f.State);
            Run(f, _scenario.Fire.FireAtS + 0.05f);
            f.FedReachedOne();
            Assert.AreEqual(Outcome.Failed, f.LastOutcome);
        }

        [Test]
        public void Pause_FreezesRunClock()
        {
            var f = ToState(FlowState.PreFire);
            float t = f.T;
            f.SetPaused(true);
            Run(f, 100f, 1f);
            Assert.AreEqual(t, f.T);
            Assert.AreEqual(FlowState.PreFire, f.State);
            f.SetPaused(false);
            Run(f, 1f);
            Assert.Greater(f.T, t);
        }

        [Test]
        public void ServerRunId_IsAwaited_AndLateAnswerAfterAbortIsIgnored()
        {
            var ids = new ManualRunIds();
            var f = Flow(ids);
            f.BootDone();
            f.CalibrationDone();
            Assert.AreEqual(FlowState.Calibration, f.State);
            Assert.IsTrue(f.StartingRun);
            ids.Answer("server-run-1");
            Assert.AreEqual("server-run-1", f.RunId);
            Assert.AreEqual(FlowState.Tutorial, f.State);

            var g = Flow(ids);
            g.BootDone();
            g.CalibrationDone();
            g.Abort("staff");
            ids.Answer("late");
            Assert.AreEqual(FlowState.Aborted, g.State);
            Assert.IsNull(g.RunId);
        }

        [Test]
        public void RunStarting_FiresBeforeRunStart()
        {
            var f = Flow();
            var order = new List<string>();
            f.RunStarting += a => order.Add("starting " + a + " " + _rec.Events.Count);
            f.RunStarted += id => order.Add("started " + _rec.Events.Count);
            f.BootDone();
            f.StartAtPreFire();
            CollectionAssert.AreEqual(new[] { "starting 1 0", "started 1" }, order);
            Assert.AreEqual(FlowState.PreFire, f.State);
        }
    }
}
