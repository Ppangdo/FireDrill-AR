using System;
using FireDrill.Core;

namespace FireDrill.Runtime
{
    /// <summary>Where GameFlow writes the run log. Implemented by RunRecorder.</summary>
    public interface IRunRecorder
    {
        void BeginRun(string runId);
        void Record(string type, object data = null);
        void EndRun();
    }

    /// <summary>Gives a run_id for a new attempt: the server's when online, a local GUID otherwise.
    /// <paramref name="done"/> is called exactly once, on the main thread.</summary>
    public interface IRunIdSource
    {
        void CreateRun(int attempt, Action<string> done);
    }

    /// <summary>Local-only run ids (offline mode, tests).</summary>
    public sealed class LocalRunIds : IRunIdSource
    {
        public void CreateRun(int attempt, Action<string> done) => done(Guid.NewGuid().ToString());
    }

    /// <summary>The 5장 state machine. The only owner of the flow state and run_id; others subscribe.
    /// Plain C#: Bootstrap calls <see cref="Tick"/> every frame with scaled delta time.</summary>
    public sealed class GameFlow
    {
        private readonly Scenario _scenario;
        private readonly RuntimeConfig.FlowSettings _cfg;
        private readonly IRunRecorder _recorder;
        private readonly IRunIdSource _runIds;

        private float _preFireAt;      // run t when PreFire started
        private float _fireDelay;      // fire_at_s or retry_fire_at_s
        private float _endingLeft;
        private int _pendingRequest;   // increments per run request; stale callbacks are ignored

        public FlowState State { get; private set; } = FlowState.Boot;
        public string RunId { get; private set; }
        public int Attempt { get; private set; }
        /// <summary>Run time (s): 0 at RUN_START, frozen while paused or outside a run.</summary>
        public float T { get; private set; }
        /// <summary>Run t of ignition, null before Fire.</summary>
        public float? FireT { get; private set; }
        public bool Paused { get; private set; }
        public bool RunActive { get; private set; }
        public bool StartingRun { get; private set; }
        public Outcome? LastOutcome { get; private set; }

        public event Action<FlowState, FlowState> StateChanged;
        /// <summary>Before RUN_START is logged (attempt number): place the trainee at the spawn point here.</summary>
        public event Action<int> RunStarting;
        public event Action<string> RunStarted;
        public event Action<Outcome> RunEnded;
        public event Action<bool> PausedChanged;

        public GameFlow(Scenario scenario, RuntimeConfig.FlowSettings cfg, IRunRecorder recorder, IRunIdSource runIds)
        {
            _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _runIds = runIds ?? throw new ArgumentNullException(nameof(runIds));
        }

        // ---- transitions driven by other modules ---------------------------

        /// <summary>Boot finished (configs loaded, server checked) → Calibration.</summary>
        public void BootDone()
        {
            if (State == FlowState.Boot) Go(FlowState.Calibration);
        }

        /// <summary>Staff confirmed calibration → first run, Tutorial.</summary>
        public void CalibrationDone()
        {
            if (State == FlowState.Calibration && !StartingRun) StartRun(1, FlowState.Tutorial);
        }

        /// <summary>Start a run directly at PreFire (retry-style; play scripts with start = prefire).</summary>
        public void StartAtPreFire()
        {
            if ((State == FlowState.Calibration || State == FlowState.Report) && !StartingRun)
                StartRun(Attempt + 1, FlowState.PreFire);
        }

        /// <summary>Errand done or tutorial time limit → PreFire.</summary>
        public void TutorialDone()
        {
            if (State == FlowState.Tutorial) EnterPreFire(_scenario.Fire.FireAtS);
        }

        /// <summary>From Report: new run, tutorial skipped.</summary>
        public void Retry()
        {
            if (State == FlowState.Report && !StartingRun) StartRun(Attempt + 1, FlowState.PreFire);
        }

        public void ReachedExit()
        {
            if (State == FlowState.Fire) EndRun(Outcome.Escaped);
        }

        public void FedReachedOne()
        {
            if (State == FlowState.Fire) EndRun(Outcome.Failed);
        }

        /// <summary>Emergency stop from any state (명세서 5장, 10장): immediately Aborted.</summary>
        public void Abort(string by)
        {
            if (State == FlowState.Aborted) return;
            _pendingRequest++;
            StartingRun = false;
            var from = State;
            if (RunActive)
            {
                _recorder.Record(EventTypes.Abort, new { by });
                _recorder.Record(EventTypes.StateChange, new { from = from.ToString(), to = nameof(FlowState.Aborted) });
                State = FlowState.Aborted;
                FinishRun(Outcome.Aborted);
            }
            else State = FlowState.Aborted;
            SetPaused(false);
            StateChanged?.Invoke(from, FlowState.Aborted);
        }

        /// <summary>WebSocket watchdog: pause the run clock and effects while the server is silent.</summary>
        public void SetPaused(bool paused)
        {
            if (Paused == paused) return;
            Paused = paused;
            PausedChanged?.Invoke(paused);
        }

        public void Tick(float dt)
        {
            if (dt <= 0) return;
            if (State == FlowState.Ending)
            {
                _endingLeft -= dt;
                if (_endingLeft <= 0) Go(FlowState.Report);
                return;
            }
            if (!RunActive || Paused) return;
            T += dt;
            if (State == FlowState.PreFire && T - _preFireAt >= _fireDelay)
            {
                FireT = T;
                Go(FlowState.Fire);
            }
            else if (State == FlowState.Fire && FireT.HasValue && T - FireT.Value >= _scenario.TimeoutS)
            {
                EndRun(Outcome.Timeout);
            }
        }

        // ---- internals -----------------------------------------------------

        private void StartRun(int attempt, FlowState first)
        {
            StartingRun = true;
            int request = ++_pendingRequest;
            _runIds.CreateRun(attempt, runId =>
            {
                if (request != _pendingRequest) return; // aborted or superseded meanwhile
                StartingRun = false;
                var from = State;
                RunId = runId;
                Attempt = attempt;
                T = 0;
                FireT = null;
                LastOutcome = null;
                RunActive = true;
                RunStarting?.Invoke(attempt);
                _recorder.BeginRun(runId);
                _recorder.Record(EventTypes.RunStart, new { scenario_id = _scenario.Id, attempt });
                RunStarted?.Invoke(runId);
                if (first == FlowState.PreFire)
                    EnterPreFire(attempt > 1 && _scenario.Fire.RetryFireAtS.HasValue ? _scenario.Fire.RetryFireAtS.Value
                                                                                      : _scenario.Fire.FireAtS, from);
                else Go(first, from);
            });
        }

        private void EnterPreFire(float fireDelay, FlowState? from = null)
        {
            _preFireAt = T;
            _fireDelay = fireDelay;
            Go(FlowState.PreFire, from);
        }

        private void EndRun(Outcome outcome)
        {
            Go(FlowState.Ending);
            _endingLeft = _cfg.EndingS;
            FinishRun(outcome);
        }

        private void FinishRun(Outcome outcome)
        {
            LastOutcome = outcome;
            _recorder.Record(EventTypes.RunEnd, new { outcome = outcome.ToString().ToLowerInvariant() });
            _recorder.EndRun();
            RunActive = false;
            RunEnded?.Invoke(outcome);
        }

        private void Go(FlowState to, FlowState? fromOverride = null)
        {
            var from = fromOverride ?? State;
            State = to;
            if (RunActive) _recorder.Record(EventTypes.StateChange, new { from = from.ToString(), to = to.ToString() });
            StateChanged?.Invoke(from, to);
        }
    }
}
