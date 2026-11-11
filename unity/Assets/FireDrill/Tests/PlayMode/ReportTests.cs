using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FireDrill.Core;
using FireDrill.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace FireDrill.Tests.PlayMode
{
    /// <summary>Stage 5 gate (명세서 15장): the report shows within 10 s after the run ends, and with the server off
    /// it shows the template report. Also the client-side 10 s deadline against a server that never answers.</summary>
    public class ReportTests
    {
        private const float ReportDeadlineS = 10f;

        [TearDown]
        public void TearDown() => Harness.Cleanup();

        private static Bootstrap LaunchWalk(string serverUrl, bool offline)
        {
            BootstrapOptions.Reset();
            BootstrapOptions.Platform = "scripted";
            BootstrapOptions.Offline = offline;
            BootstrapOptions.ServerUrl = serverUrl;
            BootstrapOptions.LogDir = Paths.Out;
            BootstrapOptions.ParticipantCode = "T01";
            BootstrapOptions.PlatformArgs = new Dictionary<string, string> { ["playscript"] = Paths.Playscript("walk_play"), ["start"] = "prefire" };
            return Harness.Launch();
        }

        private static IEnumerator ToReport(Bootstrap boot, float timeoutS = 120f)
        {
            var player = (ScriptedPlayer)boot.Platform;
            yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Report || player.Error != null, timeoutS,
                () => $"stuck in {boot.Flow.State} t={boot.Flow.T:0.0} paused={boot.Flow.Paused} server={boot.Server.Mode} step {player.CurrentStep}");
            Assert.IsNull(player.Error, player.Error);
        }

        private static void AssertScreenShowsModel(Bootstrap boot)
        {
            var view = boot.ReportScreen;
            Assert.IsTrue(view.Visible, "report screen visible in Report state");
            Assert.AreSame(boot.Report.Current, view.Model);
            Assert.AreEqual(boot.Report.Current.Text.Summary, view.SummaryText);
            Assert.GreaterOrEqual(view.ItemRows, 8, "MVP items listed");
            Assert.IsNotNull(UiFont.Source, "a Korean font loaded (file or OS fallback)");
        }

        [UnityTest]
        public IEnumerator Online_ServerSentencesArriveWithin10s()
        {
            using (var server = new TestServer())
            {
                yield return server.Start();
                var boot = LaunchWalk(server.Url, offline: false);
                yield return ToReport(boot);
                var m = boot.Report.Current;
                Assert.IsNotNull(m, "report started at RUN_END");
                Assert.IsNotNull(m.Text, "template shown at once");
                yield return Harness.WaitFor(() => !m.Pending, ReportDeadlineS, () => "server report still pending after 10 s");
                Assert.IsNull(m.Error, m.Error);
                Assert.AreEqual("fake", m.Source, "LLM_MODE=fake answers");
                Assert.LessOrEqual(m.TextArrivedAfterS.Value, ReportDeadlineS);
                yield return null;
                AssertScreenShowsModel(boot);
                StringAssert.Contains("fake", boot.ReportScreen.SourceText);
                yield return Shot(boot, "report_online");
            }
        }

        [UnityTest]
        public IEnumerator ServerKilledMidRun_TemplateReportWithin10s()
        {
            var server = new TestServer();
            try
            {
                yield return server.Start();
                var boot = LaunchWalk(server.Url, offline: false);
                yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Fire, 60f, () => "no fire");
                server.Dispose(); // the laptop server goes away during the run
                yield return ToReport(boot, 180f);
                var m = boot.Report.Current;
                yield return Harness.WaitFor(() => !m.Pending, ReportDeadlineS, () => "report pending after 10 s");
                Assert.AreEqual("template", m.Source);
                Assert.AreEqual(Evaluator.TemplateReport(m.Evaluation, boot.Config.Evaluation).Summary, m.Text.Summary);
                yield return null;
                AssertScreenShowsModel(boot);
            }
            finally { server.Dispose(); }
        }

        [UnityTest]
        public IEnumerator Offline_TemplateReportAtOnce()
        {
            var boot = LaunchWalk("http://127.0.0.1:9", offline: true);
            yield return ToReport(boot);
            var m = boot.Report.Current;
            Assert.IsFalse(m.Pending);
            Assert.AreEqual("template", m.Source);
            yield return null;
            AssertScreenShowsModel(boot);
            yield return Shot(boot, "report_offline");
        }

        /// <summary>A stub that answers /health and never answers anything else.</summary>
        private sealed class SilentServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly List<TcpClient> _held = new List<TcpClient>();
            public string Url { get; }

            public SilentServer()
            {
                _listener.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
                _ = Task.Run(Loop);
            }

            private async Task Loop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        var s = c.GetStream();
                        var buf = new byte[8192];
                        int n = await s.ReadAsync(buf, 0, buf.Length);
                        var head = Encoding.ASCII.GetString(buf, 0, n);
                        if (head.StartsWith("GET /health"))
                        {
                            var body = "{\"schema_version\":1,\"ok\":true,\"llm\":\"fake\",\"server_time\":\"now\"}";
                            var resp = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                            var bytes = Encoding.ASCII.GetBytes(resp);
                            await s.WriteAsync(bytes, 0, bytes.Length);
                            c.Close();
                        }
                        else lock (_held) _held.Add(c); // never answer
                    });
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                lock (_held) foreach (var c in _held) c.Close();
            }
        }

        [UnityTest]
        public IEnumerator ClientDeadline_KeepsTemplateWhenServerNeverAnswers()
        {
            using (var stub = new SilentServer())
            {
                var config = ConfigSet.Load();
                config.App.ServerUrl = stub.Url;
                config.App.Offline = false;
                config.Runtime.Network.ReportTimeoutS = 1f; // the real value is 10 s; the rule is the same
                using (var client = new ServerClient(config.App, config.Runtime.Network, config.Runtime.Flow.RunCreateWaitS))
                {
                    bool? online = null;
                    client.CheckHealth(ok => online = ok);
                    yield return Harness.WaitFor(() => { client.Update(0f); return online.HasValue; }, 10f, () => "health");
                    Assert.IsTrue(online.Value, client.LastError);

                    var c = new ReportController(config.Evaluation, client);
                    var ev = Evaluator.Evaluate(new List<LogEvent>(), config.Graph, config.Scenario, config.Evaluation);
                    float start = Time.realtimeSinceStartup;
                    c.Start("run-x", 1, ev, null);
                    var m = c.Current;
                    Assert.IsTrue(m.Pending);
                    yield return Harness.WaitFor(() => { client.Update(0f); return !m.Pending; }, 5f, () => "deadline not applied");
                    float took = Time.realtimeSinceStartup - start;
                    Assert.AreEqual("template", m.Source);
                    StringAssert.Contains("timeout", m.Error);
                    Assert.That(took, Is.InRange(0.9f, 3f));
                }
            }
        }

        private static IEnumerator Shot(Bootstrap boot, string name)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) yield break;
            var rt = new RenderTexture(1920, 1080, 24);
            boot.ReportScreen.SetTargetTexture(rt);
            for (int i = 0; i < 3; i++) yield return null;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var dir = Path.Combine(Paths.Repo, "out", "screenshots", "stage5");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            boot.ReportScreen.SetTargetTexture(null);
            rt.Release();
        }
    }
}
