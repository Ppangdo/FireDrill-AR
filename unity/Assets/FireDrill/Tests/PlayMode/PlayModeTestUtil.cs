using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using FireDrill.Runtime;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace FireDrill.Tests.PlayMode
{
    internal static class Paths
    {
        public static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        public static string Out => Path.Combine(Repo, "out", "playmode");
        public static string Playscript(string name) => Path.Combine(Repo, "contracts", "playscripts", name + ".json");
    }

    /// <summary>Starts the real FastAPI server (명세서 15장 3단계: "로그 파일과 서버 DB에 같은 이벤트 수").
    /// Uses server/.venv when present, otherwise uv. LLM_MODE=fake, fresh SQLite file under out/playmode.</summary>
    internal sealed class TestServer : IDisposable
    {
        public const string Token = "dev-token-change-me";
        public const string Device = "pc-01";
        private Process _proc;
        public string Url { get; private set; }
        public string Log => _log.ToString();
        private readonly System.Text.StringBuilder _log = new System.Text.StringBuilder();

        public IEnumerator Start(float timeoutS = 40f)
        {
            Directory.CreateDirectory(Paths.Out);
            int port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            var db = Path.Combine(Paths.Out, $"server_{port}.db").Replace('\\', '/');
            if (File.Exists(db)) File.Delete(db);

            var venvPython = Path.Combine(Paths.Repo, "server", ".venv", "Scripts", "python.exe");
            var uvicorn = $"-m uvicorn app.main:app --app-dir server --host 127.0.0.1 --port {port} --ws wsproto";
            var psi = File.Exists(venvPython)
                ? new ProcessStartInfo(venvPython, uvicorn)
                : new ProcessStartInfo("uv", "run --project server python " + uvicorn);
            psi.WorkingDirectory = Paths.Repo;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.EnvironmentVariables["DATABASE_URL"] = "sqlite:///" + db;
            psi.EnvironmentVariables["LLM_MODE"] = "fake";
            psi.EnvironmentVariables["DEVICE_TOKENS"] = $"{Device}:{Token},staff:staff-token";
            psi.EnvironmentVariables["ANTHROPIC_API_KEY"] = "";
            _proc = Process.Start(psi);
            _proc.OutputDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            float waited = 0;
            while (waited < timeoutS)
            {
                var t = Get("health");
                while (!t.IsCompleted) yield return null;
                if (t.Status == TaskStatus.RanToCompletion && t.Result.StatusCode == HttpStatusCode.OK) yield break;
                if (_proc.HasExited) throw new Exception("server exited:\n" + Log);
                yield return new WaitForSecondsRealtime(0.5f);
                waited += 0.5f;
            }
            throw new TimeoutException("server did not start:\n" + Log);
        }

        public Task<HttpResponseMessage> Get(string path) => Send(HttpMethod.Get, path);
        public Task<HttpResponseMessage> Post(string path) => Send(HttpMethod.Post, path);

        private async Task<HttpResponseMessage> Send(HttpMethod method, string path)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                var req = new HttpRequestMessage(method, Url + "/" + path);
                req.Headers.Add("X-Device-Token", Token);
                var resp = await http.SendAsync(req);
                await resp.Content.LoadIntoBufferAsync();
                return resp;
            }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            if (_proc == null || _proc.HasExited) return;
            try
            {
                // uv starts python as a child: end the whole tree.
                Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {_proc.Id}") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(5000);
            }
            catch (Exception e) { Debug.LogWarning("could not stop server: " + e.Message); }
        }
    }

    internal static class Harness
    {
        /// <summary>Creates a Bootstrap like the scene does, with test options already set.</summary>
        public static Bootstrap Launch()
        {
            var go = new GameObject("Bootstrap");
            return go.AddComponent<Bootstrap>();
        }

        public static IEnumerator WaitFor(Func<bool> condition, float timeoutRealS, Func<string> describe)
        {
            float start = Time.realtimeSinceStartup;
            while (!condition())
            {
                if (Time.realtimeSinceStartup - start > timeoutRealS) throw new TimeoutException(describe());
                yield return null;
            }
        }

        public static void Cleanup()
        {
            foreach (var b in Object.FindObjectsByType<Bootstrap>()) Object.DestroyImmediate(b.gameObject);
            Time.timeScale = 1f;
            BootstrapOptions.Reset();
        }

        public static string[] FileLines(string path) => File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToArray();
    }
}
