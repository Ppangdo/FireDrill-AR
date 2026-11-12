using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FireDrill.Core;
using FireDrill.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FireDrill.Tests.PlayMode
{
    /// <summary>Stage 3 gate (명세서 15장): the scene builds with floor and walls in every zone, the scripted player
    /// walks to the exit, and the log file holds the same events as the server DB. Also: staff abort over the
    /// WebSocket reaches Aborted within 1 s (11장 GameFlow).</summary>
    public class WalkableDemoTests
    {
        [TearDown]
        public void TearDown() => Harness.Cleanup();

        // ---- scene -----------------------------------------------------------

        [UnityTest]
        public IEnumerator SceneBuilder_EveryZoneHasFloorCeilingAndWalls()
        {
            var cfg = ConfigSet.Load();
            var root = new GameObject("Root");
            var view = SceneBuilder.Build(cfg.Graph, cfg.Runtime.Scene, root.transform);
            var signs = view.GetComponentsInChildren<TextMesh>();
            foreach (var room in new[] { "301", "302", "303" })
                Assert.IsTrue(signs.Any(sign => sign.text == room), "room sign " + room);
            Assert.IsTrue(signs.Any(sign => sign.text == "WATER"), "faucet sign");
            Assert.IsTrue(signs.All(sign => sign.GetComponent<Renderer>().sharedMaterial.shader.name == "FireDrill/WorldText"));
            yield return new WaitForFixedUpdate(); // colliders registered

            var graph = cfg.Graph;
            foreach (var zone in graph.Zones)
            {
                Assert.IsTrue(view.Zones.ContainsKey(zone.Id), zone.Id);
                var zv = view.Zones[zone.Id];
                Assert.IsNotNull(zv.Floor.GetComponent<MeshCollider>(), zone.Id);
                Assert.IsNotNull(zv.Light, zone.Id);

                // Floor under the zone's interior points belongs to this zone.
                var inside = InteriorPoint(zone);
                Assert.IsTrue(Physics.Raycast(new Vector3(inside.x, 1f, inside.y), Vector3.down, out var hit, 2f), zone.Id + " floor");
                Assert.AreEqual(zv.Floor, hit.collider.gameObject, zone.Id + " floor object");
                Assert.IsTrue(Physics.Raycast(new Vector3(inside.x, 1f, inside.y), Vector3.up, out hit, 3f), zone.Id + " ceiling");
                Assert.AreEqual(zone.CeilingH, hit.point.y, 1e-3f, zone.Id + " ceiling height");

                // Walls: every 0.25 m along each polygon side there is a wall, except inside door/opening gaps.
                var poly = zone.Polygon;
                int walls = 0;
                for (int i = 0; i < poly.Count; i++)
                {
                    var a = new Vector2(poly[i].X, poly[i].Z);
                    var b = new Vector2(poly[(i + 1) % poly.Count].X, poly[(i + 1) % poly.Count].Z);
                    float len = Vector2.Distance(a, b);
                    for (float s = 0.15f; s < len - 0.15f; s += 0.25f)
                    {
                        var p = Vector2.Lerp(a, b, s / len);
                        if (graph.Edges.Any(e => GapState(e, p) == 1)) continue; // within 6 cm of a gap end: either is fine
                        var gap = graph.Edges.FirstOrDefault(e => GapState(e, p) == 2);
                        bool wallHere = Physics.OverlapSphere(new Vector3(p.x, 1.0f, p.y), 0.02f)
                                               .Any(c => view.Walls.Contains(c.gameObject));
                        if (gap == null) { Assert.IsTrue(wallHere, $"{zone.Id}: no wall at {p}"); walls++; }
                        else
                        {
                            Assert.IsFalse(wallHere, $"{zone.Id}: wall inside gap {gap.Id} at {p}");
                            if (gap.Type == EdgeType.Door)
                                Assert.IsTrue(Physics.OverlapSphere(new Vector3(p.x, 2.35f, p.y), 0.02f).Any(c => view.Walls.Contains(c.gameObject)),
                                              $"{gap.Id}: no lintel above the door at {p}");
                        }
                    }
                }
                Assert.Greater(walls, 0, zone.Id);
            }

            // Doors: closed doors block, open doors do not.
            foreach (var e in graph.Edges.Where(x => x.Type == EdgeType.Door))
            {
                var door = view.Doors[e.Id];
                var c = new Vector3(e.Center.X, 1f, e.Center.Z);
                Assert.IsFalse(door.IsOpen, e.Id);
                Assert.IsTrue(Physics.OverlapSphere(c, 0.05f).Any(x => x.GetComponent<InteractTarget>()?.TargetId == e.Id), e.Id + " closed blocks");
                door.SetOpen(true, instant: true);
                yield return new WaitForFixedUpdate();
                Assert.IsFalse(Physics.OverlapSphere(c, 0.05f).Any(x => x.enabled && x.GetComponent<InteractTarget>()?.TargetId == e.Id), e.Id + " open passes");
            }
            Assert.AreEqual(2, view.ExitSigns.Count);
            Object.Destroy(root);
        }

        private static Vector2 InteriorPoint(Zone z)
        {
            var c = new Vector2(z.Polygon.Average(p => p.X), z.Polygon.Average(p => p.Z));
            return c; // demo zones are convex
        }

        /// <summary>0 = not at this gap, 1 = near a gap end (ambiguous), 2 = inside the gap.</summary>
        private static int GapState(Edge e, Vector2 p)
        {
            var c = new Vector2(e.Center.X, e.Center.Z);
            var along = e.WallAxis == WallAxis.X ? Vector2.right : Vector2.up;
            var d = p - c;
            float across = Mathf.Abs(d.x * along.y - d.y * along.x);
            if (across > 0.06f) return 0;
            float a = Mathf.Abs(Vector2.Dot(d, along));
            return a <= e.Width / 2f - 0.06f ? 2 : a <= e.Width / 2f + 0.06f ? 1 : 0;
        }

        // ---- scripted walk -----------------------------------------------------

        private static Bootstrap LaunchWalk(string serverUrl, bool offline, out ScriptedPlayer player)
        {
            BootstrapOptions.Reset();
            BootstrapOptions.Platform = "scripted";
            BootstrapOptions.Offline = offline;
            BootstrapOptions.ServerUrl = serverUrl;
            BootstrapOptions.LogDir = Paths.Out;
            BootstrapOptions.ParticipantCode = "T01";
            BootstrapOptions.PlatformArgs = new Dictionary<string, string> { ["playscript"] = Paths.Playscript("walk_play"), ["start"] = "prefire" };
            var boot = Harness.Launch();
            player = (ScriptedPlayer)boot.Platform;
            return boot;
        }

        private static IEnumerator PlayToReport(Bootstrap boot, ScriptedPlayer player)
        {
            yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Report || player.Error != null, 90f,
                () => $"stuck in {boot.Flow.State} t={boot.Flow.T:0.0} step {player.CurrentStep} head {player.Head}");
            Assert.IsNull(player.Error, player.Error);
        }

        private static void AssertEscapedRun(Bootstrap boot)
        {
            Assert.AreEqual(Outcome.Escaped, boot.Flow.LastOutcome);
            var events = boot.Recorder.Events;
            Assert.AreEqual("RUN_START", events.First().Type);
            Assert.AreEqual(1.0f, events.First().HeadRatio, 0.005f, "standing head ratio");
            var exitEnter = events.ToList().FindIndex(e => e.Type == "ZONE_ENTER" && (string)e.Data["to"] == "z_exit_stair_b");
            var ending = events.ToList().FindIndex(e => e.Type == "STATE_CHANGE" && (string)e.Data["to"] == "Ending");
            Assert.That(exitEnter, Is.GreaterThan(0).And.LessThan(ending), "ZONE_ENTER exit before Fire>Ending");
            Assert.AreEqual("RUN_END", events.Last().Type);
            CollectionAssert.AreEqual(Enumerable.Range(1, events.Count).ToArray(), events.Select(e => e.Seq).ToArray(), "seq 1..n");
            Assert.IsTrue(events.Any(e => e.Type == "DOOR_OPEN" && (string)e.Data["edge"] == "e_302_door" && (bool)e.Data["checked_before"]));
            Assert.IsTrue(events.Any(e => e.Type == "ZONE_ENTER" && (string)e.Data["to"] == "z_exit_stair_b"));
            Assert.IsTrue(events.Any(e => e.Type == "CUE_EXPOSED" && (string)e.Data["cue"] == "sound"));
            Assert.Greater(events.Count(e => e.Type == "SAMPLE"), 10);

            var lines = Harness.FileLines(boot.LastRunFile);
            Assert.AreEqual(events.Count, lines.Length, "file lines == events");
            Assert.AreEqual(events.Last().ToLine(), lines.Last());
            Assert.IsNotNull(boot.LastEvaluation);
            Assert.AreEqual(Outcome.Escaped, boot.LastEvaluation.Outcome);
        }

        [UnityTest]
        public IEnumerator ScriptedWalk_Offline_ReachesExit_AndWritesLogFile()
        {
            var boot = LaunchWalk("http://127.0.0.1:9", offline: true, out var player);
            yield return PlayToReport(boot, player);
            AssertEscapedRun(boot);
            Assert.AreEqual(ServerMode.Disabled, boot.Server.Mode);
            Assert.AreEqual(Path.Combine(Paths.Out, "runs", boot.Flow.RunId + ".jsonl"), boot.LastRunFile);
        }

        [UnityTest]
        public IEnumerator ScriptedWalk_Online_ServerHasSameEventCountAsFile()
        {
            using (var server = new TestServer())
            {
                yield return server.Start();
                var boot = LaunchWalk(server.Url, offline: false, out var player);
                yield return Harness.WaitFor(() => boot.Server.Mode != ServerMode.Connecting, 10f, () => "health check");
                Assert.AreEqual(ServerMode.Online, boot.Server.Mode, boot.Server.LastError);
                yield return PlayToReport(boot, player);
                AssertEscapedRun(boot);
                yield return Harness.WaitFor(() => boot.Server.Idle, 20f, () => $"upload pending {boot.Server.Pending}, {boot.Server.LastError}");
                Assert.AreEqual(ServerMode.Online, boot.Server.Mode, boot.Server.LastError);

                var export = server.Get($"runs/{boot.Flow.RunId}/export");
                while (!export.IsCompleted) yield return null;
                Assert.AreEqual(200, (int)export.Result.StatusCode, export.Result.Content.ReadAsStringAsync().Result);
                var csv = export.Result.Content.ReadAsStringAsync().Result.TrimStart('﻿');
                int rows = csv.Split('\n').Count(l => l.Trim().Length > 0) - 1; // minus header
                int fileLines = Harness.FileLines(boot.LastRunFile).Length;
                Assert.AreEqual(fileLines, rows, "server DB events == log file lines");
                Assert.AreEqual(fileLines, boot.Server.Uploaded);
            }
        }

        [UnityTest]
        public IEnumerator StaffAbort_OverWebSocket_AbortsWithinOneSecond()
        {
            using (var server = new TestServer())
            {
                yield return server.Start();
                var boot = LaunchWalk(server.Url, offline: false, out var player);
                yield return Harness.WaitFor(() => boot.Flow.State == FlowState.PreFire && boot.Server.WsConnected, 20f,
                    () => $"state {boot.Flow.State}, ws {boot.Server.WsConnected}, {boot.Server.LastError}");

                var post = server.Post($"staff/abort/{TestServer.Device}");
                float sent = Time.realtimeSinceStartup;
                while (!post.IsCompleted) yield return null;
                Assert.AreEqual(200, (int)post.Result.StatusCode, post.Result.Content.ReadAsStringAsync().Result);
                yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Aborted, 1f, () => "not aborted within 1 s");
                Assert.LessOrEqual(Time.realtimeSinceStartup - sent, 1f);

                var events = boot.Recorder.Events;
                CollectionAssert.AreEqual(new[] { "ABORT", "STATE_CHANGE", "RUN_END" }, events.Skip(events.Count - 3).Select(e => e.Type).ToArray());
                Assert.AreEqual("staff", (string)events[events.Count - 3].Data["by"]);
                Assert.AreEqual("aborted", (string)events.Last().Data["outcome"]);
                Assert.AreEqual(events.Count, Harness.FileLines(boot.LastRunFile).Length);
            }
        }
    }
}
