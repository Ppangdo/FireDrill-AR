using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>Plays a play script (명세서 15.2) as the trainee: a CharacterController body that really collides
    /// with walls and closed doors, plus intents raised like the PC input would. Platform name "scripted",
    /// PlatformArgs["playscript"] = path to the JSON file.</summary>
    public sealed class ScriptedPlayer : MonoBehaviour, IPlatform, IPlayerRig, IInteractionSource
    {
        private const float ArriveM = 0.05f;
        private const float StuckWindowS = 2f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() => PlatformRegistry.Register("scripted", Create);

        public static IPlatform Create(PlatformContext ctx)
        {
            var go = new GameObject("ScriptedPlayer");
            go.transform.SetParent(ctx.Parent, false);
            var p = go.AddComponent<ScriptedPlayer>();
            p.Init(ctx);
            return p;
        }

        private RuntimeConfig.PcControls _pc;
        private ZoneGraph _graph;
        private float _standing, _head, _headTarget;
        private CharacterController _body;
        private Playscript _script;
        private Bootstrap _boot;
        private float _prevTimeScale = 1f;

        private int _step = -1;
        private bool _stepStarted;
        private float _stepTimer;
        private readonly Queue<Vector3> _waypoints = new Queue<Vector3>();
        private Vector3 _stuckFrom;
        private float _stuckTimer;
        private readonly HashSet<string> _seen = new HashSet<string>();

        public Playscript Script => _script;
        public bool Finished { get; private set; }
        public string Error { get; private set; }
        public int StepIndex => _step;
        public string CurrentStep => _script != null && _step >= 0 && _step < _script.Steps.Count ? _script.Steps[_step].ToString() : null;

        // ---- IPlatform / IPlayerRig / IInteractionSource ---------------------

        public IPlayerRig Rig => this;
        public IInteractionSource Input => this;
        public ISpaceProvider Space { get; } = new PlatformRegistry.IdentitySpace();
        public bool InputEnabled { get; set; }
        public Camera Camera => null;
        public void SetShake(float amplitudeDeg, float hz) { }
#pragma warning disable 67 // play scripts choose through Bootstrap.Npcs
        public event Action<int> ChoiceKey;
#pragma warning restore 67
        /// <summary>The controller floats skinWidth above the floor; the head height is measured from the floor.</summary>
        public Vec3 Head { get { var p = transform.position; return new Vec3(p.x, Mathf.Max(0f, p.y - _body.skinWidth) + _head, p.z); } }
        public Vec3 Forward { get { var f = transform.forward; return new Vec3(f.x, f.y, f.z); } }
        public float StandingHeight => _standing;
        public event Action<Intent> IntentRaised;
        public string FocusedTargetId => null;

        public void Teleport(Vec3 feet, float yawDeg)
        {
            _body.enabled = false;
            transform.SetPositionAndRotation(new Vector3(feet.X, feet.Y, feet.Z), Quaternion.Euler(0, yawDeg, 0));
            _body.enabled = true;
        }

        private void Init(PlatformContext ctx)
        {
            _pc = ctx.Config.Runtime.Pc;
            _graph = ctx.Config.Graph;
            _standing = ctx.Config.Evaluation.StandingHeadM;
            _head = _headTarget = _standing;
            _body = gameObject.AddComponent<CharacterController>();
            _body.radius = _pc.BodyRadiusM;
            _body.skinWidth = Mathf.Min(_body.skinWidth, _pc.BodyRadiusM * 0.1f);
            SetBodyHeight();
            if (!ctx.Args.TryGetValue("playscript", out var path)) throw new ArgumentException("scripted platform needs PlatformArgs[\"playscript\"]");
            _script = Playscript.Parse(File.ReadAllText(path));
        }

        private void Start()
        {
            _boot = GetComponentInParent<Bootstrap>();
            _boot.Recorder.Appended += e => _seen.Add(e.Type);
            _boot.Recorder.RunBegan += _ => _seen.Clear();
            _boot.Flow.RunStarted += _ =>
            {
                if (_step >= 0) return; // the script covers the first run only
                _prevTimeScale = Time.timeScale;
                Time.timeScale = _script.TimeScale;
                _step = 0;
                _stepStarted = false;
            };
        }

        private void OnDestroy()
        {
            if (_step >= 0) Time.timeScale = _prevTimeScale;
        }

        // ---- stepping --------------------------------------------------------

        private void Update()
        {
            float dt = Time.deltaTime;
            float rate = (_standing - _pc.CrouchHeadM) / Mathf.Max(_pc.PostureSwitchS, 1e-3f);
            _head = Mathf.MoveTowards(_head, _headTarget, rate * dt);
            SetBodyHeight();

            if (Finished || Error != null || _step < 0 || !InputEnabled) return;
            if (_step >= _script.Steps.Count) { Done(); return; }
            var step = _script.Steps[_step];
            if (!_stepStarted) { StartStep(step); _stepStarted = true; }
            if (RunStep(step, dt)) { _step++; _stepStarted = false; }
        }

        private void StartStep(Playscript.Step s)
        {
            _stepTimer = 0;
            _waypoints.Clear();
            _stuckFrom = transform.position;
            _stuckTimer = 0;
            switch (s.Do)
            {
                case "walk_to":
                    _waypoints.Enqueue(new Vector3(s.Pos[0], 0, s.Pos[s.Pos.Length == 3 ? 2 : 1]));
                    break;
                case "walk_to_exit":
                    foreach (var p in _graph.PathTo(Head, s.Exit)) _waypoints.Enqueue(new Vector3(p.X, 0, p.Z));
                    break;
                case "crouch":
                    _headTarget = s.On ? _pc.CrouchHeadM : _standing;
                    break;
                case "choose":
                    if (_boot.Npcs.Talking == null) Fail("'choose' without an open conversation (talk_to first)");
                    else _boot.Npcs.Choose(s.Intent);
                    break;
            }
        }

        /// <returns>true when the step is done.</returns>
        private bool RunStep(Playscript.Step s, float dt)
        {
            _stepTimer += dt;
            switch (s.Do)
            {
                case "walk_to":
                case "walk_to_exit":
                    return Walk(dt, s);
                case "wait":
                    return _stepTimer >= s.Seconds;
                case "wait_for":
                    return _seen.Contains(s.Event);
                case "crouch":
                    return Mathf.Approximately(_head, _headTarget);
                case "intent":
                    if (_stepTimer < s.HoldS) return false;
                    IntentRaised?.Invoke(new Intent(ParseKind(s.Kind), s.Target, s.HoldS));
                    return true;
                case "choose":
                    return true;
                default:
                    Fail($"unknown step '{s.Do}'");
                    return false;
            }
        }

        private bool Walk(float dt, Playscript.Step s)
        {
            if (_waypoints.Count == 0) return true;
            var target = _waypoints.Peek();
            var pos = transform.position;
            var flat = new Vector3(target.x - pos.x, 0, target.z - pos.z);
            if (flat.magnitude <= ArriveM)
            {
                _waypoints.Dequeue();
                return _waypoints.Count == 0;
            }
            float speed = _head < _standing - 1e-3f ? _pc.CrouchSpeedMps : _pc.WalkSpeedMps;
            var move = Vector3.ClampMagnitude(flat.normalized * speed * dt, flat.magnitude);
            transform.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
            _body.Move(move + Vector3.down * (speed * dt)); // keep grounded

            _stuckTimer += dt;
            if (_stuckTimer >= StuckWindowS)
            {
                if (Vector3.Distance(_stuckFrom, transform.position) < 0.1f) Fail($"stuck at {transform.position} walking to {target}");
                _stuckFrom = transform.position;
                _stuckTimer = 0;
            }
            return false;
        }

        private void Done()
        {
            Finished = true;
            Time.timeScale = _prevTimeScale;
        }

        private void Fail(string message)
        {
            Error = $"step {_step} ({CurrentStep}): {message}";
            Debug.LogError("[ScriptedPlayer] " + Error);
            Time.timeScale = _prevTimeScale;
        }

        private void SetBodyHeight()
        {
            float h = Mathf.Max(_head + 0.1f, _body.radius * 2f);
            _body.height = h;
            _body.center = new Vector3(0, h / 2f, 0);
        }

        private static IntentKind ParseKind(string kind) =>
            (IntentKind)Enum.Parse(typeof(IntentKind), string.Concat(kind.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1))));
    }
}
