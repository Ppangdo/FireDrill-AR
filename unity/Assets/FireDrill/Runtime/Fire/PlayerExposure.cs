using System;
using FireDrill.Core;

namespace FireDrill.Runtime
{
    /// <summary>명세서 14.4 PlayerExposure: on every fixed fire tick, c at the trainee's head and the towel state feed
    /// the Core Exposure (FED). Stage changes become BODY_STAGE (1–3); FED 1.0 fails the run.</summary>
    public sealed class PlayerExposure
    {
        private readonly FireConfig _cfg;
        private readonly FireDirector _fire;
        private readonly IPlayerRig _rig;
        private readonly Inventory _inventory;
        private readonly IRunRecorder _recorder;
        private readonly GameFlow _flow;
        private Exposure _exposure;

        public float Fed => _exposure?.Fed ?? 0f;
        public int Stage => _exposure?.Stage ?? 0;
        /// <summary>c at the head on the last tick.</summary>
        public float CHead { get; private set; }
        /// <summary>Body stage changed (1–3, 4 = FED 1.0).</summary>
        public event Action<int> StageChanged;

        public PlayerExposure(FireConfig cfg, FireDirector fire, IPlayerRig rig, Inventory inventory, IRunRecorder recorder, GameFlow flow)
        {
            _cfg = cfg;
            _fire = fire;
            _rig = rig;
            _inventory = inventory;
            _recorder = recorder;
            _flow = flow;
            Reset();
            flow.RunStarting += _ => Reset();
            fire.Ticked += OnTick;
        }

        private void Reset()
        {
            _exposure = new Exposure(_cfg);
            _exposure.StageChanged += OnStage;
            CHead = 0;
        }

        private void OnTick(float t, float dt)
        {
            CHead = _fire.Engine.HeadConcentration(_rig.Head);
            _exposure.Accumulate(CHead, _inventory.Towel, dt);
        }

        private void OnStage(int stage)
        {
            if (!_exposure.Failed) _recorder.Record(EventTypes.BodyStage, new { stage });
            StageChanged?.Invoke(stage);
            if (_exposure.Failed) _flow.FedReachedOne();
        }
    }
}
