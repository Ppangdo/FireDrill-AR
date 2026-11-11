using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FireDrill.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FireDrill.Tests
{
    /// <summary>
    /// 11장 완료 기준 — 화재 엔진. Reference: contracts/tools/ref_fire.py, its output in
    /// contracts/golden/fire_demo.trace.json, and scenario.json tuning_targets (4장, confirmed 2026-11-11).
    /// </summary>
    public class FireEngineTests
    {
        const float Tolerance = 0.01f;

        static FireEngine NewEngine(out FireConfig cfg, Scenario scenario = null, ZoneGraph graph = null)
        {
            cfg = CoreTestData.Fire();
            var e = new FireEngine();
            e.Reset(graph ?? CoreTestData.Graph(), cfg, scenario ?? CoreTestData.Scenario());
            return e;
        }

        /// <summary>Ignite at run time fireAt and tick until `seconds` after ignition, calling onSecond at each whole second.</summary>
        static void Run(FireEngine e, FireConfig cfg, float seconds, Action<int> onSecond = null, float fireAt = 20f)
        {
            e.Ignite(fireAt);
            int perSecond = (int)Math.Round(1 / cfg.TickS);
            int n = (int)Math.Round(seconds / cfg.TickS);
            for (int i = 0; i <= n; i++)
            {
                if (i % perSecond == 0) onSecond?.Invoke(i / perSecond);
                if (i < n) e.Tick(fireAt + i * cfg.TickS, cfg.TickS);
            }
        }

        [Test]
        public void MatchesReferenceModel_180s()
        {
            var trace = JObject.Parse(File.ReadAllText(Path.Combine(CoreTestData.GoldenDir, "fire_demo.trace.json")));
            var zones = trace["zones"].Select(z => (string)z).ToArray();
            var rows = (JArray)trace["rows"];
            Assert.AreEqual(181, rows.Count);

            var e = NewEngine(out var cfg);
            Assert.AreEqual((float)trace["tick_s"], cfg.TickS, 1e-6, "trace was made with another tick");
            double worst = 0;
            string where = null;
            Run(e, cfg, (float)trace["seconds"], s =>
            {
                var row = rows[s];
                Assert.AreEqual(s, (double)row["t"], 1e-6);
                for (int z = 0; z < zones.Length; z++)
                {
                    double diff = Math.Abs(e.Get(zones[z]).D - (double)row["d"][z]);
                    if (diff > worst) { worst = diff; where = $"{zones[z]} at {s}s"; }
                }
            });
            TestContext.WriteLine($"max |D - D_ref| = {worst:E2} ({where})");
            Assert.Less(worst, Tolerance, where);
        }

        [Test]
        public void MeetsScenarioTuningTargets()
        {
            var scenario = CoreTestData.Scenario();
            Assert.IsNotEmpty(scenario.TuningTargets);
            var e = NewEngine(out var cfg, scenario);
            var firstBelow = scenario.TuningTargets.ToDictionary(t => t.Zone, _ => (int?)null);
            float horizon = scenario.TuningTargets.Max(t => t.AtS + (t.ToleranceS ?? 0)) + 1;
            Run(e, cfg, horizon, s =>
            {
                foreach (var t in scenario.TuningTargets)
                    if (firstBelow[t.Zone] == null && e.Get(t.Zone).SmokeLayerY < t.HeightM) firstBelow[t.Zone] = s;
            });

            foreach (var t in scenario.TuningTargets)
            {
                var hit = firstBelow[t.Zone];
                TestContext.WriteLine($"{t.Zone}: layer below {t.HeightM} m at {hit?.ToString() ?? "never"} s ({t.Kind} {t.AtS})");
                if (t.Kind == Scenario.TargetKind.ReachBelow)
                {
                    Assert.IsNotNull(hit, t.Zone);
                    Assert.That(hit.Value, Is.InRange(t.AtS - t.ToleranceS.Value, t.AtS + t.ToleranceS.Value), t.Why);
                }
                else
                {
                    Assert.That(hit == null || hit.Value >= t.AtS, t.Why);
                }
            }
        }

        [Test]
        public void ClosedDoorRooms_StayBelow02_At150s()
        {
            var e = NewEngine(out var cfg);
            Run(e, cfg, 150);
            Assert.Less(e.Get("z_room_301").D, 0.2f);
            Assert.Less(e.Get("z_lab_302").D, 0.2f);
        }

        [Test]
        public void SameInput_SameResult()
        {
            var a = NewEngine(out var cfg);
            var b = NewEngine(out _);
            Run(a, cfg, 120);
            Run(b, cfg, 120);
            foreach (var z in CoreTestData.Graph().Zones)
                Assert.AreEqual(a.Get(z.Id).D, b.Get(z.Id).D, 0f, z.Id);
        }

        [Test]
        public void ExitZones_StayClear_AndDClamped()
        {
            var e = NewEngine(out var cfg);
            var graph = CoreTestData.Graph();
            Run(e, cfg, 180, _ =>
            {
                foreach (var z in graph.Zones)
                {
                    float d = e.Get(z.Id).D;
                    if (z.Type == ZoneType.Exit) Assert.AreEqual(0f, d, z.Id);
                    Assert.That(d, Is.InRange(0f, 1f), z.Id);
                }
            });
            Assert.AreEqual(1f, e.Get("z_storage_303").D, 0.01f);
        }

        [Test]
        public void BeforeIgnition_NothingHappens()
        {
            var e = NewEngine(out var cfg);
            Assert.AreEqual(FirePhase.None, e.Phase);
            for (int i = 0; i < 100; i++) e.Tick(i * cfg.TickS, cfg.TickS);
            Assert.AreEqual(0f, e.Get("z_storage_303").D);
            Assert.AreEqual(cfg.Temp.BaseC, e.Get("z_storage_303").TempC);
            Assert.AreEqual(2.6f, e.Get("z_corr_n").SmokeLayerY, 1e-6);
            Assert.IsFalse(e.IsDoorHot("e_303_door", "z_corr_n"));
        }

        [Test]
        public void Phases_FollowConfig()
        {
            var e = NewEngine(out var cfg);
            var seen = new Dictionary<int, FirePhase>();
            Run(e, cfg, 70, s => seen[s] = e.Phase);
            Assert.AreEqual(FirePhase.Initial, seen[0]);
            Assert.AreEqual(FirePhase.Initial, seen[29]);
            Assert.AreEqual(FirePhase.Growing, seen[30]);
            Assert.AreEqual(FirePhase.Growing, seen[59]);
            Assert.AreEqual(FirePhase.Full, seen[60]);
        }

        [Test]
        public void DerivedValues_LayerAndTemperature()
        {
            var e = NewEngine(out var cfg);
            Run(e, cfg, 60);
            var s = e.Get("z_corr_n");
            Assert.AreEqual(2.6f - s.D * (2.6f - cfg.MinLayerY), s.SmokeLayerY, 1e-5);
            Assert.AreEqual(cfg.Temp.BaseC + cfg.Temp.PerDC * s.D, s.TempC, 1e-4);
            var origin = e.Get("z_storage_303");
            Assert.AreEqual(cfg.Temp.BaseC + cfg.Temp.PerDC * origin.D + cfg.Temp.OriginBonusC, origin.TempC, 1e-4);
        }

        [Test]
        public void HotDoor_OnlyTheBurningStore()
        {
            var e = NewEngine(out var cfg);
            Run(e, cfg, 20);
            Assert.IsTrue(e.IsDoorHot("e_303_door", "z_corr_n"), "fire on the other side");
            Assert.IsFalse(e.IsDoorHot("e_303_door", "z_storage_303"), "corridor behind a closed door is still cool");
            Assert.IsFalse(e.IsDoorHot("e_302_door", "z_lab_302"));
            Assert.IsFalse(e.IsDoorHot("e_302_door", "z_corr_m"));
            Assert.IsFalse(e.IsDoorHot("e_stair_b", "z_corr_s"));
            Assert.Throws<ArgumentException>(() => e.IsDoorHot("e_302_door", "z_corr_n"));
        }

        [Test]
        public void HeadConcentration_InsideOrBelowLayer()
        {
            var e = NewEngine(out var cfg);
            Run(e, cfg, 60);
            var s = e.Get("z_corr_n");
            Assert.Greater(s.D, 0.5f);
            Assert.AreEqual(s.D, e.HeadConcentration(new Vec3(1, s.SmokeLayerY + 0.1f, 20)), 1e-6);
            Assert.AreEqual(cfg.BelowLayerFactor * s.D, e.HeadConcentration(new Vec3(1, s.SmokeLayerY - 0.1f, 20)), 1e-6);
            Assert.AreEqual(0f, e.HeadConcentration(new Vec3(-10, 1.6f, 0)), "outside every zone");
        }

        [Test]
        public void DoorEvent_BurstsTheStoreOpenAt38s()
        {
            var e = NewEngine(out var cfg);
            var corrN = new Dictionary<int, float>();
            Run(e, cfg, 45, s => corrN[s] = e.Get("z_corr_n").D);
            Assert.IsTrue(e.IsDoorOpen("e_303_door"));
            Assert.Less(corrN[38] - corrN[37], 0.01f, "closed door leaks slowly");
            Assert.Greater(corrN[40] - corrN[39], 0.05f, "open door floods the corridor");
        }

        [Test]
        public void SetDoor_ChangesFlow()
        {
            var open = NewEngine(out var cfg);
            var closed = NewEngine(out _);
            open.Ignite(0); closed.Ignite(0);
            open.SetDoor("e_303_door", true);
            for (int i = 0; i < 200; i++) { open.Tick(i * 0.1f, cfg.TickS); closed.Tick(i * 0.1f, cfg.TickS); }
            Assert.Greater(open.Get("z_corr_n").D, closed.Get("z_corr_n").D * 5);
            Assert.Throws<ArgumentException>(() => open.SetDoor("e_corr_sm", true), "not a door");
        }

        [Test]
        public void BlockedEdge_CarriesNoSmoke()
        {
            var scenario = CoreTestData.Scenario();
            scenario.BlockedEdges = new List<string> { "e_corr_mn" };
            var e = NewEngine(out var cfg, scenario);
            Run(e, cfg, 120);
            Assert.AreEqual(0f, e.Get("z_corr_m").D);
            Assert.AreEqual(0f, e.Get("z_corr_s").D);
        }

        [Test]
        public void Extinguish_OnlyInitialPhaseWithinRange()
        {
            var e = NewEngine(out var cfg);
            Assert.AreEqual(ExtinguishResult.Ineffective, e.Extinguish(0, 10, 1), "not burning yet");
            Run(e, cfg, 10);
            Assert.AreEqual(ExtinguishResult.Ineffective, e.Extinguish(30, 10, cfg.ExtinguishRangeM + 0.5f), "too far");
            Assert.AreEqual(ExtinguishResult.InProgress, e.Extinguish(30, cfg.ExtinguishNeededS - 1, 1));
            Assert.AreEqual(FirePhase.Initial, e.Phase);
            Assert.AreEqual(ExtinguishResult.Extinguished, e.Extinguish(30, cfg.ExtinguishNeededS, 1));
            Assert.AreEqual(FirePhase.Extinguished, e.Phase);

            float d = e.Get("z_storage_303").D;
            for (int i = 0; i < 100; i++) e.Tick(40 + i * cfg.TickS, cfg.TickS);
            Assert.LessOrEqual(e.Get("z_storage_303").D, d, "no more smoke from the origin");
            Assert.AreEqual(cfg.Temp.BaseC + cfg.Temp.PerDC * e.Get("z_storage_303").D, e.Get("z_storage_303").TempC, 1e-4, "no fire heat");
        }

        [Test]
        public void Extinguish_IneffectiveOnceGrowing()
        {
            var e = NewEngine(out var cfg);
            Run(e, cfg, 35);
            Assert.AreEqual(FirePhase.Growing, e.Phase);
            Assert.AreEqual(ExtinguishResult.Ineffective, e.Extinguish(55, 20, 1));
            Assert.AreEqual(FirePhase.Growing, e.Phase);
        }

        [Test]
        public void Reset_StartsOver()
        {
            var e = NewEngine(out var cfg);
            Run(e, cfg, 60);
            e.Reset(CoreTestData.Graph(), cfg, CoreTestData.Scenario());
            Assert.AreEqual(FirePhase.None, e.Phase);
            Assert.AreEqual(0f, e.Get("z_storage_303").D);
            Assert.IsFalse(e.IsDoorOpen("e_303_door"), "scenario.initial_doors again");
        }
    }
}
