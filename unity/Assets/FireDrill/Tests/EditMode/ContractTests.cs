using System.IO;
using System.Linq;
using FireDrill.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FireDrill.Tests
{
    /// <summary>Stage 0 gate on the C# side: every contract file deserializes into the DTOs and round-trips.</summary>
    public class ContractTests
    {
        static string ConfigDir => Path.Combine(Application.dataPath, "FireDrill", "Config");
        static string RepoDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        static string GoldenDir => Path.Combine(RepoDir, "contracts", "golden");

        static T Load<T>(string file) => FireDrillJson.Deserialize<T>(File.ReadAllText(Path.Combine(ConfigDir, file)));

        [Test]
        public void ZoneGraphData_Deserializes()
        {
            var g = Load<ZoneGraphData>("zone_graph.json");
            Assert.AreEqual("demo_3f", g.BuildingId);
            Assert.AreEqual(9, g.Zones.Count);
            Assert.AreEqual(8, g.Edges.Count);
            var lobby = g.Zones.Single(z => z.Id == "z_elev_lobby");
            Assert.AreEqual(ZoneType.ElevatorLobby, lobby.Type);
            Assert.AreEqual(ObjectKind.ElevatorButton, lobby.Objects[0].Kind);
            var stairB = g.Edges.Single(e => e.Id == "e_stair_b");
            Assert.AreEqual(WallAxis.X, stairB.WallAxis);
            Assert.AreEqual(new Vec2(1, 0), stairB.Center);
            Assert.IsTrue(stairB.Handle.HasValue);
            Assert.IsFalse(g.Edges.Single(e => e.Id == "e_lobby").Handle.HasValue);
            Assert.AreEqual(new Vec3(6, 0, 13), g.Zones.Single(z => z.Id == "z_lab_302").Spawn);
        }

        [Test]
        public void Scenario_Deserializes()
        {
            var s = Load<Scenario>("scenario.json");
            Assert.AreEqual("z_storage_303", s.Fire.OriginZone);
            Assert.AreEqual(2, s.Npcs.Count);
            Assert.AreEqual(NpcAction.GoElevator, s.Npcs.Single(n => n.Id == "npc_junho").InitialAction);
            Assert.AreEqual(CueKind.Alarm, s.Cues.Last().Cue);
            Assert.AreEqual(Scenario.DoorAction.BurstOpen, s.DoorEvents.Single().Action);
            Assert.AreEqual("o_document", s.Tutorial.ErrandObject);
            Assert.AreEqual(3, s.TuningTargets.Count);
            Assert.AreEqual(Scenario.TargetKind.StayAbove, s.TuningTargets.Last().Kind);
        }

        [Test]
        public void FireConfig_Deserializes()
        {
            var f = Load<FireConfig>("fire.json");
            Assert.AreEqual(0.1f, f.TickS, 1e-6);
            Assert.AreEqual(60f, f.Temp.PerDC);
            Assert.AreEqual(0.01f, f.EdgeK["e_corr_sm"], 1e-6);
            Assert.AreEqual(4, f.Fed.Stages.Count);
            Assert.AreEqual(0.6f, f.Fed.Towel.Wet, 1e-6);
        }

        [Test]
        public void EvaluationConfig_Deserializes()
        {
            var e = Load<EvaluationConfig>("evaluation.json");
            Assert.AreEqual(10, e.Items.Count);
            Assert.AreEqual(8, e.Items.Count(i => i.Mvp));
            Assert.AreEqual(100f, e.Items.Where(i => i.Mvp).Sum(i => i.Weight) + 10f, "MVP weights + one non-MVP sanity");
            foreach (var i in e.Items) Assert.IsTrue(e.Rules.ContainsKey(i.RuleRef), i.Id);
        }

        [Test]
        public void Dialogue_NpcCache_AppConfig_Deserialize()
        {
            var d = Load<DialogueData>("dialogue.json");
            Assert.IsTrue(d.Nodes.ContainsKey(d.Start));
            var c = Load<NpcCache>("npc_cache.json");
            Assert.IsTrue(c.Entries.ContainsKey(NpcCache.Key("npc_minji", PlayerAction.Warn, 2, NpcAction.Freeze)));
            Assert.AreEqual(NpcAction.FollowPlayer, c.Entries["npc_minji|warn|2|freeze"].Action);
            var a = Load<AppConfig>("app_config.json");
            Assert.AreEqual("s_demo_fire", a.ScenarioId);
        }

        [TestCase("model_run")]
        [TestCase("fail_run")]
        [TestCase("na_run")]
        public void GoldenLog_ParsesAndRoundTrips(string name)
        {
            var lines = File.ReadAllLines(Path.Combine(GoldenDir, name + ".jsonl")).Where(l => l.Length > 0).ToArray();
            Assert.IsNotEmpty(lines);
            for (int i = 0; i < lines.Length; i++)
            {
                var e = LogEvent.ParseLine(lines[i]);
                Assert.AreEqual(i + 1, e.Seq);
                CollectionAssert.Contains(EventTypes.All.ToList(), e.Type);
                var back = JObject.Parse(e.ToLine());
                var orig = JObject.Parse(lines[i]);
                CollectionAssert.AreEquivalent(orig.Properties().Select(p => p.Name), back.Properties().Select(p => p.Name), $"{name} line {i + 1}");
            }
            Assert.AreEqual(EventTypes.RunStart, LogEvent.ParseLine(lines[0]).Type);
            Assert.AreEqual(EventTypes.RunEnd, LogEvent.ParseLine(lines[lines.Length - 1]).Type);
        }

        [Test]
        public void EvaluationResult_SerializesSnakeCase()
        {
            var r = new EvaluationResult { RunId = "x", Outcome = Outcome.Escaped, TotalPct = 100 };
            r.Items.Add(new ItemResult { Id = "alert", Grade = Grade.Na, RuleRef = "FIRE-01" });
            var j = JObject.Parse(FireDrillJson.Serialize(r));
            Assert.AreEqual("escaped", (string)j["outcome"]);
            Assert.AreEqual(1, (int)j["schema_version"]);
            Assert.AreEqual("na", (string)j["items"][0]["grade"]);
            Assert.IsNotNull(j["weak_zones"]);
        }
    }
}
