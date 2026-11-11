using System.Linq;
using FireDrill.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FireDrill.Tests
{
    /// <summary>11장 완료 기준 — 구역 그래프: load, area, exit distance, blocked edges, field-path errors.</summary>
    public class ZoneGraphTests
    {
        static readonly Vec3 LabCentre = new Vec3(6, 1.6f, 13);

        [Test]
        public void Load_DemoGraph()
        {
            var g = CoreTestData.Graph();
            Assert.AreEqual(9, g.Zones.Count);
            Assert.AreEqual(8, g.Edges.Count);
            Assert.AreEqual("302호 실습실", g.Zone("z_lab_302").Name);
            Assert.IsTrue(g.IsExit("z_exit_stair_a"));
            Assert.IsTrue(g.IsExit("z_exit_stair_b"));
            Assert.IsFalse(g.IsExit("z_corr_s"));
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => g.Zone("z_nope"));
        }

        [TestCase("z_corr_s", 16)]
        [TestCase("z_elev_lobby", 16)]
        [TestCase("z_lab_302", 48)]
        [TestCase("z_storage_303", 32)]
        [TestCase("z_exit_stair_a", 6)]
        public void Area_FromPolygon(string zone, double area)
        {
            Assert.AreEqual(area, CoreTestData.Graph().Area(zone), 1e-9);
        }

        [Test]
        public void EdgesOf_ListsBothDirections()
        {
            var g = CoreTestData.Graph();
            CollectionAssert.AreEquivalent(new[] { "e_302_door", "e_corr_sm", "e_corr_mn", "e_lobby" }, g.EdgesOf("z_corr_m").Select(e => e.Id));
            CollectionAssert.AreEquivalent(new[] { "e_stair_b" }, g.EdgesOf("z_exit_stair_b").Select(e => e.Id));
        }

        [Test]
        public void ZoneAt_PolygonContainment()
        {
            var g = CoreTestData.Graph();
            Assert.AreEqual("z_lab_302", g.ZoneAt(LabCentre));
            Assert.AreEqual("z_corr_s", g.ZoneAt(new Vec3(1, 1.6f, 4)));
            Assert.AreEqual("z_elev_lobby", g.ZoneAt(new Vec3(-2, 0, 12)));
            Assert.AreEqual("z_exit_stair_b", g.ZoneAt(new Vec3(1, 0, -1.5f)));
            Assert.IsNull(g.ZoneAt(new Vec3(-10, 0, 0)));
            Assert.IsNull(g.ZoneAt(new Vec3(6, 0, 17)), "gap between 302 and 303");
            Assert.IsNotNull(g.ZoneAt(new Vec3(2, 0, 13)), "points on a shared wall belong to a zone");
        }

        // Expected values: contracts/tools/ref_eval.py ZoneGraph.exit_distance.
        [TestCase(6f, 13f, 15.16227766)]
        [TestCase(1f, 4f, 4.0)]
        [TestCase(-2f, 12f, 14.12310563)]
        [TestCase(6f, 20f, 8.12310563)]
        public void ExitDistance_MatchesReference(float x, float z, double expected)
        {
            Assert.AreEqual(expected, CoreTestData.Graph().ExitDistance(new Vec3(x, 1.6f, z)).Value, 1e-4);
        }

        [Test]
        public void ExitDistance_ExitIsZero_OutsideIsNull()
        {
            var g = CoreTestData.Graph();
            Assert.AreEqual(0f, g.ExitDistance(new Vec3(1, 1.6f, 25)));
            Assert.IsNull(g.ExitDistance(new Vec3(-10, 0, 0)));
        }

        [Test]
        public void ExitDistance_DecreasesAlongTheCorridor()
        {
            var g = CoreTestData.Graph();
            float prev = float.MaxValue;
            for (float z = 7.5f; z >= 0.5f; z -= 1f)
            {
                float d = g.ExitDistance(new Vec3(1, 1.6f, z)).Value;
                Assert.Less(d, prev);
                prev = d;
            }
        }

        [Test]
        public void SetBlocked_ExcludesEdgeFromDistancesAndPaths()
        {
            var g = CoreTestData.Graph();
            g.SetBlocked("e_corr_mn", true);
            Assert.IsTrue(g.IsBlocked("e_corr_mn"));
            Assert.AreEqual(17.09901951, g.ExitDistance(LabCentre).Value, 1e-4, "reference with e_corr_mn blocked");
            var path = g.PathToExit(LabCentre);
            Assert.AreEqual(new Vec3(2, 1.6f, 13), path[0]);
            Assert.AreEqual(new Vec3(1, 1.6f, 8), path[1]);
            Assert.AreEqual(new Vec3(1, 1.6f, 0), path[2]);
            Assert.AreEqual("z_exit_stair_b", g.ZoneAt(path[3]));

            g.SetBlocked("e_corr_mn", false);
            Assert.AreEqual(15.16227766, g.ExitDistance(LabCentre).Value, 1e-4);
        }

        [Test]
        public void SetBlocked_AllExitsCutOff_IsInfiniteAndNoPath()
        {
            var g = CoreTestData.Graph();
            g.SetBlocked("e_302_door", true);
            Assert.IsTrue(float.IsPositiveInfinity(g.ExitDistance(LabCentre).Value));
            Assert.IsEmpty(g.PathToExit(LabCentre));
        }

        [Test]
        public void PathToExit_UsesNearestExit()
        {
            var g = CoreTestData.Graph();
            var path = g.PathToExit(LabCentre);
            CollectionAssert.AreEqual(
                new[] { new Vec3(2, 1.6f, 13), new Vec3(1, 1.6f, 16), new Vec3(1, 1.6f, 24) },
                path.Take(3).ToArray());
            Assert.AreEqual(4, path.Count);
            Assert.AreEqual("z_exit_stair_a", g.ZoneAt(path[3]));
        }

        [Test]
        public void PathTo_Zone()
        {
            var g = CoreTestData.Graph();
            var path = g.PathTo(LabCentre, "z_elev_lobby");
            CollectionAssert.AreEqual(new[] { new Vec3(2, 1.6f, 13), new Vec3(0, 1.6f, 12), new Vec3(-2, 1.6f, 12) }, path.ToArray());

            var home = g.PathTo(new Vec3(1, 1.6f, 9), "z_lab_302");
            Assert.AreEqual(new Vec3(6, 1.6f, 13), home.Last(), "spawn of z_lab_302");

            var already = g.PathTo(LabCentre, "z_lab_302");
            Assert.AreEqual(1, already.Count);
            Assert.IsEmpty(g.PathTo(new Vec3(-10, 0, 0), "z_lab_302"));
        }

        // ── invalid JSON names the bad field ────────────────────────────────────

        static string Mutated(System.Action<JObject> edit)
        {
            var j = JObject.Parse(CoreTestData.ConfigText("zone_graph.json"));
            edit(j);
            return j.ToString();
        }

        static void AssertError(string json, string path)
        {
            var ex = Assert.Throws<ZoneGraphException>(() => ZoneGraph.Load(json));
            StringAssert.StartsWith(path + ":", ex.Message);
        }

        [Test] public void Invalid_MissingField() => AssertError(Mutated(j => ((JObject)j["zones"][2]).Remove("ceiling_h")), "$.zones[2].ceiling_h");
        [Test] public void Invalid_BadEnum() => AssertError(Mutated(j => j["zones"][0]["type"] = "hallway"), "$.zones[0].type");
        [Test] public void Invalid_BadVector() => AssertError(Mutated(j => j["edges"][1]["center"] = new JArray(1)), "$.edges[1].center");
        [Test] public void Invalid_PolygonPoint() => AssertError(Mutated(j => j["zones"][1]["polygon"][2] = new JArray(1, "x")), "$.zones[1].polygon[2][1]");
        [Test] public void Invalid_UnknownField() => AssertError(Mutated(j => j["edges"][0]["colour"] = "red"), "$.edges[0].colour");
        [Test] public void Invalid_UnknownZoneReference() => AssertError(Mutated(j => j["edges"][3]["to"] = "z_nowhere"), "$.edges[3].to");
        [Test] public void Invalid_DuplicateZone() => AssertError(Mutated(j => j["zones"][1]["id"] = "z_corr_s"), "$.zones[1].id");
        [Test] public void Invalid_BadId() => AssertError(Mutated(j => j["edges"][0]["id"] = "door1"), "$.edges[0].id");
        [Test] public void Invalid_NegativeWidth() => AssertError(Mutated(j => j["edges"][0]["width"] = -1), "$.edges[0].width");
        [Test] public void Invalid_SchemaVersion() => AssertError(Mutated(j => j["schema_version"] = 2), "$.schema_version");

        [Test]
        public void Invalid_NoExit() => AssertError(Mutated(j =>
        {
            foreach (var z in j["zones"]) if ((string)z["type"] == "exit") z["type"] = "stair";
        }), "$.zones");

        [Test]
        public void Invalid_NotJson()
        {
            Assert.Throws<ZoneGraphException>(() => ZoneGraph.Load("{\"zones\": [1, 2"));
        }
    }
}
