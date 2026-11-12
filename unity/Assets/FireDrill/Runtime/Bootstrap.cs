using System;
using System.Collections.Generic;
using System.Linq;
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
        public static bool DebugOverlay;            // developer overlay visible from the start (F1 toggles it)
        public static Dictionary<string, string> PlatformArgs = new Dictionary<string, string>();

        public static void Reset()
        {
            ConfigDir = LogDir = Platform = ServerUrl = ParticipantCode = null;
            Offline = null;
            DebugOverlay = false;
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
        public ItemInteractions Items { get; private set; }
        public NpcDirector Npcs { get; private set; }
        public PlayerExposure Exposure { get; private set; }
        public SmokeRenderer Smoke { get; private set; }
        public FireVfx FireView { get; private set; }
        public BodyEffects Body { get; private set; }
        public AudioFx Audio { get; private set; }
        public ReportController Report { get; private set; }
        public ReportView ReportScreen { get; private set; }
        public DialogueRunner Dialogue { get; private set; }
        public UiRoot Ui { get; private set; }
        private DebugOverlay _overlay;
        /// <summary>Current one-line subtitle and when it disappears (realtime).</summary>
        public string Subtitle { get; private set; }
        private LineRenderer _sprayStream;
        private bool _nearActiveFire;
        private float _subtitleUntil;
        public ServerClient Server { get; private set; }
        public EvaluationResult LastEvaluation { get; private set; }
        public string LastRunFile { get; private set; }
        public int LastRunEventCount { get; private set; }

        private bool _waitingForStart;
        private bool _bootReady;
        private bool _autoBegin;

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

            Font signFont = null;
            if (Font.GetOSInstalledFontNames().Contains(rt.Ui.FallbackOsFont))
                signFont = Font.CreateDynamicFontFromOSFont(rt.Ui.FallbackOsFont, rt.Ui.FontSamplingPt);
            Building = SceneBuilder.Build(Config.Graph, rt.Scene, transform, signFont);

            var engine = new FireEngine();
            Platform = PlatformRegistry.Create(BootstrapOptions.Platform ?? app.Platform, new PlatformContext
            {
                Config = Config, Parent = transform, Building = Building, Args = BootstrapOptions.PlatformArgs,
            });
            GameFlow flow = null;
            Recorder = new RunRecorder(Config.Graph, engine, Platform.Rig, () => flow.T, () => Exposure?.Fed ?? 0f,
                                       Config.Evaluation, rt.Flow, BootstrapOptions.LogDir);
            Server = new ServerClient(app, rt.Network, rt.Flow.RunCreateWaitS);
            Flow = flow = new GameFlow(Config.Scenario, rt.Flow, Recorder, Server);
            Fire = new FireDirector(engine, Config.Graph, Config.Fire, Config.Scenario, Flow, Recorder, rt.Flow.TickS)
            {
                SmokeCueD = rt.Effects.SmokeCueD,
                TraineeZone = () => Recorder.CurrentZone,
            };
            Doors = new DoorInteractions(Building, Config.Graph, Config.Scenario, Fire, Recorder, Platform.Rig, Flow, rt.Pc,
                                         Platform.Input, Config.Evaluation);
            Items = new ItemInteractions(Building, Config.Graph, Config.Scenario, Config.Fire, rt, Fire, Recorder, Platform.Rig,
                                         Flow, Platform.Input);
            var brain = new NpcBrain(Config.NpcCache, Server);
            Npcs = new NpcDirector(Config, engine, Doors, brain, Recorder, Platform.Rig, Flow, Fire, Platform.Input, transform);
            Npcs.Said += (npc, line) => Say($"{npc.Name}: {line}");
            Exposure = new PlayerExposure(Config.Fire, Fire, Platform.Rig, Items.Inventory, Recorder, Flow);
            BuildEffects(engine, rt);
            BuildReport();
            Dialogue = new DialogueRunner(Config.Dialogue, Config.Scenario, Flow, Items.Inventory, Config.Graph, Platform.Rig, rt.Ui);
            var uiGo = new GameObject("UiRoot");
            uiGo.transform.SetParent(transform, false);
            Ui = uiGo.AddComponent<UiRoot>();
            Ui.Init(this);
            Platform.ChoiceKey += i =>
            {
                if (Npcs.Talking != null) Npcs.ChooseIndex(i);
                else Dialogue?.ChooseIndex(i);
            };
            Platform.DebugToggle += () => { if (_overlay != null) _overlay.enabled = !_overlay.enabled; };

            Recorder.Appended += Server.Enqueue;
            Recorder.RunFinished += OnRunFinished;
            Server.AbortReceived += by => Flow.Abort(by);
            Server.WatchdogChanged += paused => Flow.SetPaused(paused);
            Server.StatusProvider = Status;
            Flow.RunStarting += PlaceTrainee;
            Flow.StateChanged += OnStateChanged;

            PlaceTrainee(1);
            _overlay = gameObject.AddComponent<DebugOverlay>();
            _overlay.Init(this);
            _overlay.enabled = BootstrapOptions.DebugOverlay; // F1 shows it (developer view)

            Server.CheckHealth(online =>
            {
                Debug.Log($"[FireDrill] server {(online ? "online" : "offline")} ({app.ServerUrl}) llm={Server.Llm}");
                _bootReady = true; // continue in Update so every component has run Start()
            });
        }

        private void BuildEffects(IFireEngine engine, RuntimeConfig rt)
        {
            var fx = new GameObject("Effects");
            fx.transform.SetParent(transform, false);
            Smoke = fx.AddComponent<SmokeRenderer>();
            Smoke.Init(Config.Graph, engine, Platform.Rig, Config.Fire, rt);
            var fireGo = new GameObject("Fire");
            fireGo.transform.SetParent(fx.transform, false);
            FireView = fireGo.AddComponent<FireVfx>();
            FireView.Init(Config.Graph, Config.Scenario.Fire.OriginZone, engine, rt);
            Body = fx.AddComponent<BodyEffects>();
            Body.Init(Platform, rt);
            Audio = fx.AddComponent<AudioFx>();
            Audio.Init(FireView.transform.position, rt);
            var spray = new GameObject("ExtinguisherStream");
            spray.transform.SetParent(fx.transform, false);
            _sprayStream = spray.AddComponent<LineRenderer>();
            _sprayStream.sharedMaterial = Materials.Transparent(new Color(0.96f, 0.98f, 1f, 0.64f));
            _sprayStream.positionCount = 2;
            _sprayStream.startWidth = 0.055f;
            _sprayStream.endWidth = 0.34f;
            _sprayStream.enabled = false;

            var subs = rt.Effects.Subtitles;
            Fire.Ticked += (t, dt) => Smoke.OnTick();
            Fire.CueExposed += cue =>
            {
                var key = cue.ToString().ToLowerInvariant();
                if (subs.TryGetValue(key, out var text)) Say(text);
                if (cue == CueKind.Alarm) Audio.SetAlarm(true);
            };
            Exposure.StageChanged += stage =>
            {
                Body.SetStage(Mathf.Min(stage, 3));
                if (subs.TryGetValue("stage_" + stage, out var text)) Say(text);
            };
            Doors.HandleChecked += (edge, hot) =>
            {
                if (hot) { Body.HotHandle(); Audio.HotHandle(); }
                Say(subs[hot ? "hot_handle" : "cool_handle"]);
            };
            Items.AlarmPressed += () =>
            {
                if (Flow.State == FlowState.Fire)
                {
                    Say(subs["alarm"]);
                    Audio.SetAlarm(true);
                }
                else
                {
                    Say("화재경보 발신기를 눌렀습니다. 지금은 화재 상황이 아닙니다.");
                    Audio.ConfirmAlarmPress();
                }
            };
            Items.TowelWetted += () => Say("수건을 적셨습니다. T로 코·입에 댈 수 있습니다.");
            Items.Sprayed += success =>
            {
                if (success) Say("초기 화재를 진화했습니다. 남은 연기를 피해 대피하세요.");
            };
            Flow.RunStarted += _ =>
            {
                Smoke.Snap();
                FireView.ResetFire();
                Body.SetStage(0);
                Audio.StopAll();
                Subtitle = null;
                _nearActiveFire = false;
            };
            Flow.StateChanged += (from, to) =>
            {
                if (to == FlowState.Ending || to == FlowState.Report || to == FlowState.Aborted)
                    SetEffects(false);
                else if (to == FlowState.PreFire) SetEffects(true);
            };
            Flow.PausedChanged += paused => SetEffects(!paused && Flow.RunActive);
        }

        private void BuildReport()
        {
            Report = new ReportController(Config.Evaluation, Server);
            var go = new GameObject("ReportView");
            go.transform.SetParent(transform, false);
            ReportScreen = go.AddComponent<ReportView>();
            ReportScreen.Init(Config);
            Report.Changed += ReportScreen.OnModelChanged;
            ReportScreen.RetryClicked += () => Flow.Retry();
            ReportScreen.QuitClicked += Quit;
            Flow.StateChanged += (from, to) =>
            {
                if (to == FlowState.Report && Report.Current != null) ReportScreen.Show(Report.Current);
                else if (from == FlowState.Report || to == FlowState.Aborted) ReportScreen.Hide();
            };
        }

        public static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>Aborted or paused: every effect off (명세서 5장, 10장).</summary>
        private void SetEffects(bool on)
        {
            Smoke.SetVisible(on);
            FireView.SetVisible(on);
            Body.SetVisible(on);
            if (!on)
            {
                Audio.StopAll();
                _sprayStream.enabled = false;
            }
        }

        /// <summary>One-line subtitle for subtitle_s (16.11).</summary>
        public void Say(string text)
        {
            Subtitle = text;
            _subtitleUntil = Time.realtimeSinceStartup + Config.Runtime.Effects.SubtitleS;
        }

        private void OnStateChanged(FlowState from, FlowState to)
        {
            Debug.Log($"[FireDrill] {from} -> {to} (t={Flow.T:0.0})");
            if (to == FlowState.Calibration)
            {
                if (BootstrapOptions.ParticipantCode != null) _autoBegin = true; // next frame: no nested state changes
                else _waitingForStart = true;
            }
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
                // Report: score and template now, server sentences when they arrive (명세서 8장). No report after an abort.
                var npcResults = Npcs.FinishRun(LastEvaluation.Outcome);
                if (LastEvaluation.Outcome != Outcome.Aborted)
                {
                    Report.Start(runId, Flow.Attempt, LastEvaluation, Flow.FireT);
                    Report.Current.NpcResults = npcResults.Select(kv => (Npcs.Agents[kv.Key].Name, kv.Value)).ToList();
                }
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
                                    Recorder.CurrentZone ?? Config.Graph.ZoneAt(head), Exposure.Fed, Exposure.Stage);
        }

        private void Update()
        {
            if (Flow == null) return;
            Server.Update(Time.unscaledDeltaTime);
            if (_bootReady && Flow.State == FlowState.Boot) Flow.BootDone();
            else if (_autoBegin) { _autoBegin = false; Begin(BootstrapOptions.ParticipantCode); }
            UpdateExtinguisher(Time.deltaTime);
            Flow.Tick(Time.deltaTime);
            Fire.Update();
            var firePhase = Fire.Engine.Phase;
            bool nearActiveFire = Flow.State == FlowState.Fire && !Flow.Paused &&
                firePhase != FirePhase.None && firePhase != FirePhase.Extinguished &&
                Config.Graph.ZoneAt(Platform.Rig.Head) == Config.Scenario.Fire.OriginZone;
            if (nearActiveFire && !_nearActiveFire) Say("화점 가까이입니다. 즉시 안전한 쪽으로 물러나세요.");
            _nearActiveFire = nearActiveFire;
            Npcs.Update(Time.deltaTime);
            Dialogue.Update(Time.deltaTime);
            bool playing = Flow.State == FlowState.Tutorial || Flow.State == FlowState.PreFire || Flow.State == FlowState.Fire;
            Platform.UiFocus = !playing || Flow.Paused || Ui.WantsCursor;
            Recorder.Update(); // ZONE_ENTER into the exit is logged before the run ends
            if (Subtitle != null && Time.realtimeSinceStartup > _subtitleUntil) Subtitle = null;
            var phase = Fire.Engine.Phase;
            Audio.SetFire(Flow.State == FlowState.Fire && !Flow.Paused &&
                (phase == FirePhase.Initial || phase == FirePhase.Growing || phase == FirePhase.Full));
            if (Flow.State == FlowState.Fire)
            {
                var zone = Recorder.CurrentZone;
                if (zone != null && Config.Graph.IsExit(zone)) Flow.ReachedExit();
            }
            Platform.InputEnabled = !Flow.Paused &&
                (Flow.State == FlowState.Tutorial || Flow.State == FlowState.PreFire || Flow.State == FlowState.Fire);
        }

        private void UpdateExtinguisher(float dt)
        {
            var camera = Platform.Camera;
            bool held = Platform.Input is IContinuousSprayInput input && input.SprayHeld && camera != null &&
                Items.Inventory.HandKind == "extinguisher";
            bool aimedAtFire = false;
            Vector3 end = default;
            if (held)
            {
                var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                float range = Config.Fire.ExtinguishRangeM;
                end = ray.GetPoint(range);
                if (Physics.Raycast(ray, out var hit, range, ~0, QueryTriggerInteraction.Collide))
                {
                    end = hit.point;
                    aimedAtFire = hit.collider.GetComponentInParent<InteractTarget>()?.TargetId == ItemInteractions.FireTarget;
                }
            }

            Items.UpdateSpray(held, aimedAtFire, dt);
            Audio.SetSpray(Items.IsSpraying);
            _sprayStream.enabled = Items.IsSpraying;
            if (!Items.IsSpraying) return;
            var view = camera.transform;
            _sprayStream.SetPosition(0, view.TransformPoint(new Vector3(0.23f, -0.22f, 0.35f)));
            _sprayStream.SetPosition(1, end);
        }

        private void OnDestroy()
        {
            Recorder?.Dispose();
            Server?.Dispose();
        }
    }
}
