using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FireDrill.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FireDrill.Runtime
{
    public enum ServerMode { Disabled, Connecting, Online, Offline }

    /// <summary>Device status pushed over the WebSocket every ws_status_interval_s (명세서 14.6).</summary>
    public readonly struct DeviceStatus
    {
        public readonly string RunId, State, Zone;
        public readonly float T, Fed;
        public readonly int Stage;
        public DeviceStatus(string runId, string state, float t, string zone, float fed, int stage)
        {
            RunId = runId; State = state; T = t; Zone = zone; Fed = fed; Stage = stage;
        }
    }

    /// <summary>REST + WebSocket client (명세서 9장, 14.6).
    /// <list type="bullet">
    /// <item>Events go to the server in batches every upload_interval_s; (run_id, seq) makes resends harmless.</item>
    /// <item>4xx is not retried. 5xx / network errors are retried after retry_delays_s, then the client goes offline,
    /// keeps the queue and probes /health every offline_probe_s.</item>
    /// <item>WebSocket: status out, abort and ping in. While online and in a run, silence longer than ws_ping_timeout_s
    /// pauses the run (only when app_config.offline is false).</item>
    /// </list>
    /// All network work runs on tasks; results are applied on the main thread in <see cref="Update"/>.</summary>
    public sealed class ServerClient : IRunIdSource, IDisposable
    {
        private readonly AppConfig _app;
        private readonly RuntimeConfig.NetworkSettings _net;
        private readonly float _runCreateWaitS;
        private readonly HttpClient _http;
        private readonly ConcurrentQueue<Action> _main = new ConcurrentQueue<Action>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly Queue<LogEvent> _queue = new Queue<LogEvent>();
        private readonly HashSet<string> _localRuns = new HashSet<string>();     // runs the server does not know
        private readonly List<(string runId, FinishRunRequest body)> _finishes = new List<(string, FinishRunRequest)>();
        private bool _inFlight;
        private float _uploadTimer, _probeTimer, _statusTimer;
        private int _retry;
        private float _retryWait;
        private string _sessionId;

        private ClientWebSocket _ws;
        private bool _wsConnecting;
        private float _wsRetryTimer;
        private DateTime _lastWsMessageUtc = DateTime.UtcNow;

        public ServerMode Mode { get; private set; }
        public string LastError { get; private set; }
        public string Llm { get; private set; }
        public int Uploaded { get; private set; }
        public int Pending => _queue.Count;
        public bool WsConnected => _ws != null && _ws.State == WebSocketState.Open;
        public bool Idle => _queue.Count == 0 && _finishes.Count == 0 && !_inFlight;

        /// <summary>Main thread: staff pressed abort (by = "staff" | "system").</summary>
        public event Action<string> AbortReceived;
        /// <summary>Main thread: true when the ping watchdog pauses the run, false when messages are back.</summary>
        public event Action<bool> WatchdogChanged;
        public Func<DeviceStatus?> StatusProvider;
        private bool _watchdogPaused;

        public ServerClient(AppConfig app, RuntimeConfig.NetworkSettings net, float runCreateWaitS)
        {
            _app = app;
            _net = net;
            _runCreateWaitS = runCreateWaitS;
            Mode = app.Offline ? ServerMode.Disabled : ServerMode.Connecting;
            _http = new HttpClient { BaseAddress = new Uri(app.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(net.RequestTimeoutS) };
            _http.DefaultRequestHeaders.Add("X-Device-Token", app.DeviceToken);
        }

        public bool Online => Mode == ServerMode.Online;

        // ---- boot / session / run ------------------------------------------

        /// <summary>Boot: GET /health. <paramref name="done"/> gets true when online.</summary>
        public void CheckHealth(Action<bool> done)
        {
            if (Mode == ServerMode.Disabled) { done(false); return; }
            Send<HealthResponse>(HttpMethod.Get, "health", null, (ok, res, err) =>
            {
                Mode = ok && res.Ok ? ServerMode.Online : ServerMode.Offline;
                Llm = res?.Llm;
                if (!ok) LastError = err;
                done(Online);
            });
        }

        public void CreateSession(string participantCode, Action<bool> done)
        {
            if (!Online) { done(false); return; }
            var body = new CreateSessionRequest { BuildingId = _app.BuildingId, ParticipantCode = participantCode, DeviceId = _app.DeviceId };
            Send<CreateSessionResponse>(HttpMethod.Post, "sessions", body, (ok, res, err) =>
            {
                if (ok) _sessionId = res.SessionId.ToString();
                else GoOffline(err);
                done(ok);
            });
        }

        /// <summary>IRunIdSource: the server's run_id, or a local one (that run is then kept local only).</summary>
        public void CreateRun(int attempt, Action<string> done)
        {
            bool answered = false;
            void Answer(string id, bool local)
            {
                if (answered) return;
                answered = true;
                if (local) _localRuns.Add(id);
                done(id);
            }
            if (!Online || _sessionId == null) { Answer(Guid.NewGuid().ToString(), true); return; }
            var body = new CreateRunRequest { ScenarioId = _app.ScenarioId, Attempt = attempt };
            Send<CreateRunResponse>(HttpMethod.Post, $"sessions/{_sessionId}/runs", body, (ok, res, err) =>
            {
                if (ok) Answer(res.RunId.ToString(), false);
                else { GoOffline(err); Answer(Guid.NewGuid().ToString(), true); }
            });
            Later(_runCreateWaitS, () => Answer(Guid.NewGuid().ToString(), true));
        }

        // ---- events --------------------------------------------------------

        /// <summary>RunRecorder.Appended subscriber.</summary>
        public void Enqueue(LogEvent e)
        {
            if (Mode == ServerMode.Disabled || _localRuns.Contains(e.RunId)) return;
            _queue.Enqueue(e);
        }

        /// <summary>Send queued events now (RUN_END).</summary>
        public void Flush() => _uploadTimer = _net.UploadIntervalS;

        /// <summary>POST /runs/{id}/finish once that run's events are uploaded.</summary>
        public void Finish(string runId, Outcome outcome, EvaluationResult evaluation)
        {
            if (Mode == ServerMode.Disabled || _localRuns.Contains(runId)) return;
            _finishes.Add((runId, new FinishRunRequest { Outcome = outcome, Evaluation = evaluation }));
            Flush();
        }

        /// <summary>POST /runs/{id}/report (명세서 8장, 14.6). <paramref name="done"/> gets the server reply, or null
        /// when offline, the run is local, the request fails or report_timeout_s passes (the caller keeps its template).</summary>
        public void RequestReport(string runId, EvaluationResult evaluation, Action<ReportResponse, string> done)
        {
            if (!Online || _localRuns.Contains(runId)) { done(null, Online ? "local run" : "offline"); return; }
            var body = new ReportRequest { Evaluation = evaluation };
            Send<ReportResponse>(HttpMethod.Post, $"runs/{runId}/report", body,
                                 (ok, res, err, status) => done(ok ? res : null, err), _net.ReportTimeoutS);
        }

        public bool IsLocalRun(string runId) => _localRuns.Contains(runId);

        /// <summary>POST /npc/react within npc_timeout_s (명세서 8장). null when offline, failed, late or not valid
        /// (an action outside the action set fails to parse and counts as invalid).</summary>
        public void ReactNpc(NpcReactRequest request, Action<NpcReactResponse, string> done)
        {
            if (!Online) { done(null, "offline"); return; }
            Send<NpcReactResponse>(HttpMethod.Post, "npc/react", request,
                                   (ok, res, err, status) => done(ok ? res : null, err), _net.NpcTimeoutS);
        }

        // ---- main-thread pump ----------------------------------------------

        /// <summary>Call every frame with unscaled delta time.</summary>
        public void Update(float dt)
        {
            while (_main.TryDequeue(out var a)) a();
            if (Mode == ServerMode.Disabled) return;

            if (Mode == ServerMode.Offline)
            {
                _probeTimer += dt;
                if (_probeTimer >= _net.OfflineProbeS)
                {
                    _probeTimer = 0;
                    Send<HealthResponse>(HttpMethod.Get, "health", null, (ok, res, err) =>
                    {
                        if (ok && res.Ok && Mode == ServerMode.Offline) { Mode = ServerMode.Online; _retry = 0; }
                    });
                }
                return;
            }
            if (Mode != ServerMode.Online) return;

            if (_retryWait > 0) _retryWait -= dt;
            _uploadTimer += dt;
            if (!_inFlight && _retryWait <= 0 && (_uploadTimer >= _net.UploadIntervalS || _retry > 0))
            {
                _uploadTimer = 0;
                if (_queue.Count > 0) UploadBatch();
                else if (_finishes.Count > 0) SendFinish();
            }
            UpdateWebSocket(dt);
        }

        private void UploadBatch()
        {
            var runId = _queue.Peek().RunId;
            var batch = _queue.TakeWhile(e => e.RunId == runId).Take(_net.UploadBatchMax).ToList();
            _inFlight = true;
            Send<PostEventsResponse>(HttpMethod.Post, $"runs/{runId}/events", new PostEventsRequest { Events = batch }, (ok, res, err, status) =>
            {
                _inFlight = false;
                if (ok || (status >= 400 && status < 500))
                {
                    if (!ok) Debug.LogError($"[FireDrill] events rejected ({status}), dropping {batch.Count}: {err}");
                    for (int i = 0; i < batch.Count; i++) _queue.Dequeue();
                    if (ok) Uploaded += batch.Count;
                    _retry = 0;
                }
                else Retry(err);
            });
        }

        private void SendFinish()
        {
            var (runId, body) = _finishes[0];
            _inFlight = true;
            Send<OkResponse>(HttpMethod.Post, $"runs/{runId}/finish", body, (ok, res, err, status) =>
            {
                _inFlight = false;
                if (ok || (status >= 400 && status < 500))
                {
                    if (!ok) Debug.LogError($"[FireDrill] finish rejected ({status}): {err}");
                    _finishes.RemoveAt(0);
                    _retry = 0;
                }
                else Retry(err);
            });
        }

        private void Retry(string err)
        {
            LastError = err;
            if (_retry < _net.RetryDelaysS.Count)
            {
                _retryWait = _net.RetryDelaysS[_retry];
                _retry++;
            }
            else GoOffline(err);
        }

        private void GoOffline(string err)
        {
            if (Mode == ServerMode.Disabled) return;
            LastError = err;
            Mode = ServerMode.Offline;
            _retry = 0;
            _retryWait = 0;
            _probeTimer = 0;
            CloseWebSocket();
            SetWatchdog(false);
            Debug.LogWarning("[FireDrill] server offline: " + err);
        }

        // ---- WebSocket -----------------------------------------------------

        private void UpdateWebSocket(float dt)
        {
            if (!WsConnected && !_wsConnecting)
            {
                _wsRetryTimer -= dt;
                if (_wsRetryTimer <= 0) { _wsRetryTimer = _net.WsReconnectS; _ = ConnectWebSocket(); }
            }
            if (WsConnected)
            {
                _statusTimer += dt;
                if (_statusTimer >= _net.WsStatusIntervalS)
                {
                    _statusTimer = 0;
                    var s = StatusProvider?.Invoke();
                    if (s.HasValue)
                    {
                        var v = s.Value;
                        var json = new JObject
                        {
                            ["type"] = "status", ["run_id"] = v.RunId, ["state"] = v.State, ["t"] = Math.Round(v.T, 2),
                            ["zone"] = v.Zone, ["fed"] = Math.Round(v.Fed, 4), ["stage"] = v.Stage,
                        };
                        _ = SendWs(json.ToString(Newtonsoft.Json.Formatting.None));
                    }
                }
            }
            bool silent = (DateTime.UtcNow - _lastWsMessageUtc).TotalSeconds > _net.WsPingTimeoutS;
            bool inRun = StatusProvider?.Invoke()?.RunId != null;
            SetWatchdog(silent && inRun);
        }

        private void SetWatchdog(bool paused)
        {
            if (_watchdogPaused == paused) return;
            _watchdogPaused = paused;
            WatchdogChanged?.Invoke(paused);
        }

        private async Task ConnectWebSocket()
        {
            _wsConnecting = true;
            var ws = new ClientWebSocket();
            try
            {
                var baseUri = _http.BaseAddress;
                var scheme = baseUri.Scheme == "https" ? "wss" : "ws";
                var uri = new Uri($"{scheme}://{baseUri.Authority}/ws/device/{Uri.EscapeDataString(_app.DeviceId)}?token={Uri.EscapeDataString(_app.DeviceToken)}");
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(_net.RequestTimeoutS));
                    await ws.ConnectAsync(uri, timeout.Token);
                }
                _main.Enqueue(() =>
                {
                    _ws = ws;
                    _wsConnecting = false;
                    _lastWsMessageUtc = DateTime.UtcNow;
                });
                _ = ReceiveLoop(ws);
            }
            catch (Exception e)
            {
                ws.Dispose();
                _main.Enqueue(() => { _wsConnecting = false; LastError = "ws: " + e.Message; });
            }
        }

        private async Task ReceiveLoop(ClientWebSocket ws)
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            try
            {
                while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    var text = sb.ToString();
                    sb.Clear();
                    _main.Enqueue(() => OnWsMessage(text));
                }
            }
            catch (Exception e)
            {
                _main.Enqueue(() => LastError = "ws: " + e.Message);
            }
            _main.Enqueue(() => { if (_ws == ws) { _ws = null; } ws.Dispose(); });
        }

        private void OnWsMessage(string text)
        {
            _lastWsMessageUtc = DateTime.UtcNow;
            JObject msg;
            try { msg = JObject.Parse(text); }
            catch { return; }
            if ((string)msg["type"] == "abort") AbortReceived?.Invoke((string)msg["by"] ?? "staff");
        }

        private async Task SendWs(string json)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) return;
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, _cts.Token);
            }
            catch (Exception e) { _main.Enqueue(() => LastError = "ws send: " + e.Message); }
        }

        private void CloseWebSocket()
        {
            var ws = _ws;
            _ws = null;
            if (ws == null) return;
            try { ws.Abort(); } catch { /* already closed */ }
            ws.Dispose();
        }

        // ---- HTTP helpers --------------------------------------------------

        private void Send<T>(HttpMethod method, string path, object body, Action<bool, T, string> done) =>
            Send<T>(method, path, body, (ok, res, err, status) => done(ok, res, err));

        private void Send<T>(HttpMethod method, string path, object body, Action<bool, T, string, int> done, float timeoutS = 0f)
        {
            _ = Task.Run(async () =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                if (timeoutS > 0f) deadline.CancelAfter(TimeSpan.FromSeconds(timeoutS));
                bool ok = false;
                T res = default;
                string err = null;
                int status = 0;
                try
                {
                    var req = new HttpRequestMessage(method, path);
                    if (body != null) req.Content = new StringContent(FireDrillJson.Serialize(body), Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, deadline.Token))
                    {
                        status = (int)resp.StatusCode;
                        var text = await resp.Content.ReadAsStringAsync();
                        if (resp.IsSuccessStatusCode)
                        {
                            res = FireDrillJson.Deserialize<T>(text);
                            ok = true;
                        }
                        else err = $"{status} {text}";
                    }
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { err = $"timeout after {timeoutS}s"; }
                catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
                _main.Enqueue(() => done(ok, res, err, status));
            });
        }

        private void Later(float seconds, Action action)
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), _cts.Token); }
                catch (TaskCanceledException) { return; }
                _main.Enqueue(action);
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            CloseWebSocket();
            _http.Dispose();
        }
    }
}
