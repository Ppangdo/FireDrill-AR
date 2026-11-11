using System;
using System.Collections.Generic;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>Overrides for tests and command-line runs. Set before the Bootstrap component wakes.</summary>
    public static class BootstrapOptions
    {
        public static string ConfigDir;
        public static string LogDir;
        public static string Platform;              // default: app_config.platform
        public static string ServerUrl;             // default: app_config.server_url
        public static bool? Offline;                // default: app_config.offline
        public static string ParticipantCode;       // set → start without waiting for the start button
        public static Dictionary<string, string> PlatformArgs = new Dictionary<string, string>();

        public static void Reset()
        {
            ConfigDir = LogDir = Platform = ServerUrl = ParticipantCode = null;
            Offline = null;
            PlatformArgs = new Dictionary<string, string>();
        }
    }

    /// <summary>The only object in Bootstrap.unity (명세서 16.2, 14.4): loads config, picks the platform adapter,
    /// creates and wires every module, then drives them once per frame.</summary>
    public sealed class Bootstrap : MonoBehaviour
    {
        public ConfigSet Config { get; private set; }
        public string ConfigError { get; private set; }
        public BuildingView Building { get; private set; }
        public IPlatform Platform { get; private set; }
        public GameFlow Flow { get; private set; }
        public RunRecorder Recorder { get; private set; }
        public FireDirector Fire { get; private set; }
        public DoorInteractions Doors { get; private set; }
        public ServerClient Server { get; private set; }
        public EvaluationResult LastEvaluation { get; private set; }
        public string LastRunFile { get; private set; }
        public int LastRunEventCount { get; private set; }

        private bool _waitingForStart;
        private bool _skipTutorial;
        private bool _bootReady;

        private void Awake()
        {
            try
            {
                Config = ConfigSet.Load(BootstrapOptions.ConfigDir);
            }
            catch (Exception e)
            {
                ConfigError = e.Message;
                Debug.LogError("[FireDrill] config error: " + e.Message);
                gameObject.AddComponent<DebugOverlay>().Init(this);
                return;
            }
            var app = Config.App;
            if (BootstrapOptions.ServerUrl != null) app.ServerUrl = BootstrapOptions.ServerUrl;
            if (BootstrapOptions.Offline.HasValue) app.Offline = BootstrapOptions.Offline.Value;
            var rt = Config.Runtime;

            Building = SceneBuilder.Build(Config.Graph, rt.Scene, transform);

            var engine = new ZeroFireEngine(); // stage 3: no smoke (명세서 15장)
            Platform = PlatformRegistry.Create(BootstrapOptions.Platform ?? app.Platform, new PlatformContext
            {
                Config = Config, Parent = transform, Building = Building, Args = BootstrapOptions.PlatformArgs,
            });
            GameFlow flow = null;
            Recorder = new RunRecorder(Config.Graph, engine, Platform.Rig, () => flow.T, () => 0f,
                                       Config.Evaluation, rt.Flow, BootstrapOptions.LogDir);
            Server = new ServerClient(app, rt.Network, rt.Flow.RunCreateWaitS);
            Flow = flow = new GameFlow(Config.Scenario, rt.Flow, Recorder, Server);
            Fire = new FireDirector(engine, Config.Graph, Config.Fire, Config.Scenario, Flow, Recorder, rt.Flow.TickS);
            Doors = new DoorInteractions(Building, Config.Graph, Config.Scenario, Fire, Recorder, Platform.Rig, Flow, rt.Pc, Platform.Input);

            Recorder.Appended += Server.Enqueue;
            Recorder.RunFinished += OnRunFinished;
            Server.AbortReceived += by => Flow.Abort(by);
            Server.WatchdogChanged += paused => Flow.SetPaused(paused);
            Server.StatusProvider = Status;
            Flow.RunStarting += PlaceTrainee;
            Flow.StateChanged += OnStateChanged;

            PlaceTrainee(1);
            gameObject.AddComponent<DebugOverlay>().Init(this);

            Server.CheckHealth(online =>
            {
                Debug.Log($"[FireDrill] server {(online ? "online" : "offline")} ({app.ServerUrl}) llm={Server.Llm}");
                _bootReady = true; // continue in Update so every component has run Start()
            });
        }

        private void OnStateChanged(FlowState from, FlowState to)
        {
            Debug.Log($"[FireDrill] {from} -> {to} (t={Flow.T:0.0})");
            if (to == FlowState.Calibration)
            {
                if (BootstrapOptions.ParticipantCode != null) Begin(BootstrapOptions.ParticipantCode);
                else _waitingForStart = true;
            }
            // Stage 3 has no tutorial content yet (DialogueRunner: stage 7), so the tutorial ends on the next frame.
            if (to == FlowState.Tutorial) _skipTutorial = true;
        }

        /// <summary>Start button (or test option): participant code → session → first run.</summary>
        public void Begin(string participantCode, bool startAtPreFire = false)
        {
            _waitingForStart = false;
            Server.CreateSession(participantCode, _ =>
            {
                if (startAtPreFire || (BootstrapOptions.PlatformArgs.TryGetValue("start", out var s) && s == "prefire"))
                    Flow.StartAtPreFire();
                else Flow.CalibrationDone();
            });
        }

        public bool WaitingForStart => _waitingForStart;

        private void PlaceTrainee(int attempt)
        {
            var spawn = attempt > 1 ? Config.Scenario.RetrySpawn : Config.Scenario.PlayerSpawn;
            Platform.Rig.Teleport(spawn.Pos, spawn.Yaw);
        }

        private void OnRunFinished(string runId, IReadOnlyList<LogEvent> events)
        {
            LastRunFile = Recorder.FilePath;
            LastRunEventCount = events.Count;
            try
            {
                LastEvaluation = Evaluator.Evaluate(events, Config.Graph, Config.Scenario, Config.Evaluation);
                Server.Finish(runId, LastEvaluation.Outcome, LastEvaluation);
            }
            catch (Exception e)
            {
                Debug.LogError("[FireDrill] evaluation failed: " + e);
                Server.Flush();
            }
        }

        private DeviceStatus? Status()
        {
            if (Flow == null) return null;
            var head = Platform.Rig.Head;
            return new DeviceStatus(Flow.RunActive ? Flow.RunId : null, Flow.State.ToString(), Flow.T,
                                    Recorder.CurrentZone ?? Config.Graph.ZoneAt(head), 0f, 0);
        }

        private void Update()
        {
            if (Flow == null) return;
            Server.Update(Time.unscaledDeltaTime);
            if (_bootReady && Flow.State == FlowState.Boot) Flow.BootDone();
            if (_skipTutorial) { _skipTutorial = false; Flow.TutorialDone(); }
            Flow.Tick(Time.deltaTime);
            Fire.Update();
            Recorder.Update(); // ZONE_ENTER into the exit is logged before the run ends
            if (Flow.State == FlowState.Fire)
            {
                var zone = Recorder.CurrentZone;
                if (zone != null && Config.Graph.IsExit(zone)) Flow.ReachedExit();
            }
            Platform.InputEnabled = !Flow.Paused &&
                (Flow.State == FlowState.Tutorial || Flow.State == FlowState.PreFire || Flow.State == FlowState.Fire);
        }

        private void OnDestroy()
        {
            Recorder?.Dispose();
            Server?.Dispose();
        }
    }
}
