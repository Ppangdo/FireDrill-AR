using System.Collections.Generic;
using FireDrill.Core;

namespace FireDrill.Runtime
{
    /// <summary>Drives the fire engine (명세서 14.4): fixed 0.1 s tick on run time, ignition on Fire,
    /// cues as CUE_EXPOSED (sound · smell · alarm by time after ignition or an earlier alarm press; smoke when the
    /// trainee's zone D passes effects.smoke_cue_d), scenario door_events, door state relay.</summary>
    public sealed class FireDirector
    {
        private readonly IFireEngine _engine;
        private readonly ZoneGraph _graph;
        private readonly FireConfig _cfg;
        private readonly Scenario _scenario;
        private readonly GameFlow _flow;
        private readonly IRunRecorder _recorder;
        private readonly float _tick;
        private readonly HashSet<CueKind> _exposed = new HashSet<CueKind>();
        private readonly HashSet<int> _doorEventsDone = new HashSet<int>();
        private float _engineT;

        public IFireEngine Engine => _engine;
        /// <summary>Smoke cue threshold; 0 disables the smoke cue.</summary>
        public float SmokeCueD { get; set; }
        /// <summary>Zone the trainee is in (for the smoke cue).</summary>
        public System.Func<string> TraineeZone { get; set; }
        /// <summary>Every fixed tick after the engine stepped: (run t, dt). PlayerExposure accumulates here.</summary>
        public event System.Action<float, float> Ticked;
        /// <summary>Scenario door event (edge, open): the door moves by itself, nobody logs DOOR_OPEN.</summary>
        public event System.Action<string, bool> DoorEvent;
        public event System.Action<CueKind> CueExposed;

        public FireDirector(IFireEngine engine, ZoneGraph graph, FireConfig cfg, Scenario scenario, GameFlow flow,
                            IRunRecorder recorder, float tickS)
        {
            _engine = engine;
            _graph = graph;
            _cfg = cfg;
            _scenario = scenario;
            _flow = flow;
            _recorder = recorder;
            _tick = tickS;
            _engine.Reset(graph, cfg, scenario);
            flow.RunStarted += _ => ResetForRun();
            flow.StateChanged += (from, to) =>
            {
                if (to == FlowState.Fire) _engine.Ignite(_flow.FireT ?? _flow.T);
            };
        }

        private void ResetForRun()
        {
            _graph.Data.Edges.ForEach(e => _graph.SetBlocked(e.Id, false));
            foreach (var id in _scenario.BlockedEdges ?? new List<string>()) _graph.SetBlocked(id, true);
            _engine.Reset(_graph, _cfg, _scenario);
            _exposed.Clear();
            _doorEventsDone.Clear();
            _engineT = 0;
        }

        public void SetDoor(string edgeId, bool open) => _engine.SetDoor(edgeId, open);

        /// <summary>The trainee pressed an alarm call point: the building alarm sounds now.</summary>
        public void AlarmPressed() => Expose(CueKind.Alarm);

        /// <summary>Called every frame after GameFlow.Tick.</summary>
        public void Update()
        {
            if (!_flow.RunActive || _flow.Paused) return;
            while (_engineT + _tick <= _flow.T + 1e-4f && _flow.RunActive)
            {
                _engineT += _tick;
                _engine.Tick(_engineT, _tick);
                Ticked?.Invoke(_engineT, _tick);
            }
            if (_flow.State != FlowState.Fire || !_flow.FireT.HasValue) return;
            float sinceFire = _flow.T - _flow.FireT.Value;
            foreach (var cue in _scenario.Cues)
                if (cue.Cue != CueKind.Smoke && sinceFire >= cue.AtS) Expose(cue.Cue);
            var zone = TraineeZone?.Invoke();
            if (SmokeCueD > 0 && zone != null && _engine.Get(zone).D > SmokeCueD) Expose(CueKind.Smoke);
            var events = _scenario.DoorEvents;
            for (int i = 0; i < events.Count; i++)
            {
                if (sinceFire < events[i].AtS || !_doorEventsDone.Add(i)) continue;
                bool open = events[i].Action != Scenario.DoorAction.Close;
                _engine.SetDoor(events[i].Edge, open);
                DoorEvent?.Invoke(events[i].Edge, open);
            }
        }

        private void Expose(CueKind cue)
        {
            if (_flow.State != FlowState.Fire || !_exposed.Add(cue)) return;
            _recorder.Record(EventTypes.CueExposed, new { cue = cue.ToString().ToLowerInvariant() });
            CueExposed?.Invoke(cue);
        }
    }
}
