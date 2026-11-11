using System;
using System.Collections.Generic;
using System.IO;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>The single entry point for run logs (명세서 14.4): fills the Snapshot, appends to the IEventLog,
    /// writes runs/{run_id}.jsonl, and adds ZONE_ENTER, POSTURE and 1 s SAMPLE events by itself.</summary>
    public sealed class RunRecorder : IRunRecorder, IDisposable
    {
        private readonly ZoneGraph _graph;
        private readonly IFireEngine _fire;
        private readonly IPlayerRig _rig;
        private readonly Func<float> _time;
        private readonly Func<float> _fed;
        private readonly float _postureThreshold;
        private readonly float _sampleInterval;
        private readonly string _logDir;

        private EventLog _log;
        private StreamWriter _file;
        private string _zone;
        private bool _low;
        private float _nextSample;

        public string RunId => _log?.RunId;
        public bool Recording { get; private set; }
        public string FilePath { get; private set; }
        /// <summary>Events of the current (or last finished) run.</summary>
        public IReadOnlyList<LogEvent> Events => (IReadOnlyList<LogEvent>)_log?.Events ?? Array.Empty<LogEvent>();
        public string CurrentZone => _zone;

        public event Action<LogEvent> Appended;
        public event Action<string> RunBegan;
        /// <summary>After RUN_END: run id and every event of that run.</summary>
        public event Action<string, IReadOnlyList<LogEvent>> RunFinished;

        /// <param name="logDir">Folder that receives runs/{run_id}.jsonl (default: persistentDataPath).</param>
        public RunRecorder(ZoneGraph graph, IFireEngine fire, IPlayerRig rig, Func<float> time, Func<float> fed,
                           EvaluationConfig evaluation, RuntimeConfig.FlowSettings flow, string logDir = null)
        {
            _graph = graph;
            _fire = fire;
            _rig = rig;
            _time = time;
            _fed = fed;
            _postureThreshold = evaluation.PostureThreshold;
            _sampleInterval = flow.SampleIntervalS;
            _logDir = Path.Combine(logDir ?? Application.persistentDataPath, "runs");
        }

        public void BeginRun(string runId)
        {
            CloseFile();
            _log = new EventLog(runId);
            _log.Appended += OnAppended;
            Directory.CreateDirectory(_logDir);
            FilePath = Path.Combine(_logDir, runId + ".jsonl");
            _file = new StreamWriter(FilePath, append: false) { AutoFlush = true, NewLine = "\n" };
            _zone = _graph.ZoneAt(_rig.Head);
            _low = HeadRatio < _postureThreshold;
            _nextSample = _sampleInterval;
            Recording = true;
            RunBegan?.Invoke(runId);
        }

        public void Record(string type, object data = null)
        {
            if (!Recording) return;
            _log.Append(type, Capture(), data);
        }

        public void EndRun()
        {
            if (!Recording) return;
            Recording = false;
            CloseFile();
            RunFinished?.Invoke(_log.RunId, _log.Events);
        }

        /// <summary>Called every frame after GameFlow.Tick.</summary>
        public void Update()
        {
            if (!Recording) return;
            var zone = _graph.ZoneAt(_rig.Head);
            if (zone != null && zone != _zone)
            {
                var from = _zone;
                _zone = zone;
                Record(EventTypes.ZoneEnter, new { from, to = zone, exit_dist = Round3(_graph.ExitDistance(_rig.Head)) });
            }
            bool low = HeadRatio < _postureThreshold;
            if (low != _low)
            {
                _low = low;
                Record(EventTypes.Posture, new { low });
            }
            float t = _time();
            while (Recording && t >= _nextSample)
            {
                Record(EventTypes.Sample);
                _nextSample += _sampleInterval;
            }
        }

        private float HeadRatio => _rig.StandingHeight > 0 ? _rig.Head.Y / _rig.StandingHeight : 1f;

        private Snapshot Capture()
        {
            var head = _rig.Head;
            var zone = _graph.ZoneAt(head) ?? _zone;
            var zs = zone != null ? _fire.Get(zone) : default;
            return new Snapshot(Round3(_time()), zone, new Vec3(Round3(head.X), Round3(head.Y), Round3(head.Z)),
                                Round3(HeadRatio), Round3(_fire.HeadConcentration(head)), Round4(_fed()),
                                Round4(zs.D), Round3(zs.SmokeLayerY));
        }

        private void OnAppended(LogEvent e)
        {
            _file?.WriteLine(e.ToLine());
            Appended?.Invoke(e);
        }

        private void CloseFile()
        {
            _file?.Dispose();
            _file = null;
        }

        private static float Round3(float v) => (float)Math.Round(v, 3);
        private static float? Round3(float? v) => v.HasValue ? Round3(v.Value) : (float?)null;
        private static float Round4(float v) => (float)Math.Round(v, 4);

        public void Dispose() => CloseFile();
    }
}
