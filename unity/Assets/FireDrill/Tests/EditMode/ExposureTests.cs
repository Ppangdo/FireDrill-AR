using System.Collections.Generic;
using FireDrill.Core;
using NUnit.Framework;

namespace FireDrill.Tests
{
    /// <summary>11장 완료 기준 — FED·신체 단계: c=1 fails at 60 s, wet towel at 100 s, each stage event exactly once at its threshold.</summary>
    public class ExposureTests
    {
        /// <summary>Ticks of 0.1 s at concentration c until failure; returns the tick count.</summary>
        static int TicksToFail(TowelState towel, float c = 1f)
        {
            var exp = new Exposure(CoreTestData.Fire());
            int ticks = 0;
            while (exp.Stage < 4 && ticks < 100000) { exp.Accumulate(c, towel, 0.1f); ticks++; }
            return ticks;
        }

        [Test] public void ThickSmoke_FailsAt60s() => Assert.AreEqual(600, TicksToFail(TowelState.None));
        [Test] public void WetTowel_FailsAt100s() => Assert.AreEqual(1000, TicksToFail(TowelState.Wet));
        [Test] public void DryTowel_FailsAt75s() => Assert.AreEqual(750, TicksToFail(TowelState.Dry));
        [Test] public void HalfConcentration_TakesTwiceAsLong() => Assert.AreEqual(1200, TicksToFail(TowelState.None, 0.5f));

        [Test]
        public void StageEvents_OncePerThreshold_AtTheRightTick()
        {
            var cfg = CoreTestData.Fire();
            var exp = new Exposure(cfg);
            var events = new List<(int tick, int stage, float fed)>();
            int tick = 0;
            exp.StageChanged += s => events.Add((tick, s, exp.Fed));
            for (tick = 1; tick <= 700; tick++) exp.Accumulate(1f, TowelState.None, 0.1f);

            Assert.AreEqual(4, events.Count);
            int[] expectedTicks = { 180, 360, 510, 600 }; // FED 0.30, 0.60, 0.85, 1.00 at 1/600 per tick
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(i + 1, events[i].stage);
                Assert.AreEqual(expectedTicks[i], events[i].tick, $"stage {i + 1}");
                Assert.GreaterOrEqual(events[i].fed, cfg.Fed.Stages[i] - 1e-5f);
            }
            Assert.AreEqual(4, exp.Stage);
            Assert.IsTrue(exp.Failed);
            Assert.AreEqual(1f, exp.Fed, 1e-6, "capped");
        }

        [Test]
        public void BigStep_RaisesEveryCrossedStageInOrder()
        {
            var exp = new Exposure(CoreTestData.Fire());
            var stages = new List<int>();
            exp.StageChanged += stages.Add;
            exp.Accumulate(1f, TowelState.None, 40f); // FED 0.667
            CollectionAssert.AreEqual(new[] { 1, 2 }, stages);
            Assert.AreEqual(2, exp.Stage);
        }

        [Test]
        public void Fed_NeverDecreases_AndCleanAirAddsNothing()
        {
            var exp = new Exposure(CoreTestData.Fire());
            exp.Accumulate(0.5f, TowelState.None, 6f);
            float fed = exp.Fed;
            Assert.AreEqual(0.05f, fed, 1e-6);
            exp.Accumulate(0f, TowelState.None, 100f);
            exp.Accumulate(-1f, TowelState.None, 1f);
            Assert.AreEqual(fed, exp.Fed);
            Assert.AreEqual(0, exp.Stage);
        }
    }
}
