using System;
using FireDrill.Core;
using FireDrill.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FireDrill.Platform.Pc
{
    /// <summary>PC trainee (명세서 10장, 16.7): CharacterController body, first-person camera, keyboard and mouse.
    /// Raises intents for the target under the screen centre; range and rules are checked by Interactables.</summary>
    public sealed class PcPlayer : MonoBehaviour, IPlatform, IPlayerRig, IInteractionSource
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register() => PlatformRegistry.Register("pc", Create);

        public static IPlatform Create(PlatformContext ctx)
        {
            var go = new GameObject("PcPlayer");
            go.transform.SetParent(ctx.Parent, false);
            var p = go.AddComponent<PcPlayer>();
            p.Init(ctx);
            return p;
        }

        private RuntimeConfig.PcControls _pc;
        private BuildingView _building;
        private CharacterController _body;
        private Camera _camera;
        private float _standing, _head, _headTarget, _pitch;
        private float _hHeld, _escHeld;
        private bool _hFired, _escFired;
        private bool _inputEnabled;

        // ---- IPlatform / IPlayerRig / IInteractionSource ---------------------

        public IPlayerRig Rig => this;
        public IInteractionSource Input => this;
        public ISpaceProvider Space { get; } = new PlatformRegistry.IdentitySpace();
        public Camera Camera => _camera;
        /// <summary>The controller floats skinWidth above the floor; the head height is measured from the floor.</summary>
        public Vec3 Head { get { var p = transform.position; return new Vec3(p.x, Mathf.Max(0f, p.y - _body.skinWidth) + _head, p.z); } }
        public Vec3 Forward { get { var f = _camera.transform.forward; return new Vec3(f.x, f.y, f.z); } }
        public float StandingHeight => _standing;
        public event Action<Intent> IntentRaised;
        public string FocusedTargetId { get; private set; }

        public bool InputEnabled
        {
            get => _inputEnabled;
            set
            {
                if (_inputEnabled == value) return;
                _inputEnabled = value;
                Cursor.lockState = value ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !value;
            }
        }

        public void Teleport(Vec3 feet, float yawDeg)
        {
            _body.enabled = false;
            transform.SetPositionAndRotation(new Vector3(feet.X, feet.Y, feet.Z), Quaternion.Euler(0, yawDeg, 0));
            _pitch = 0;
            _body.enabled = true;
        }

        private void Init(PlatformContext ctx)
        {
            _pc = ctx.Config.Runtime.Pc;
            _building = ctx.Building;
            _standing = ctx.Config.Evaluation.StandingHeadM;
            _head = _headTarget = _standing;
            _body = gameObject.AddComponent<CharacterController>();
            _body.radius = _pc.BodyRadiusM;
            _body.skinWidth = Mathf.Min(_body.skinWidth, _pc.BodyRadiusM * 0.1f);
            var camGo = new GameObject("Camera");
            camGo.transform.SetParent(transform, false);
            _camera = camGo.AddComponent<Camera>();
            _camera.nearClipPlane = 0.05f;
            camGo.AddComponent<AudioListener>();
            camGo.tag = "MainCamera";
            ApplyHead();
        }

        // ---- per frame ---------------------------------------------------------

        private void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            float dt = Time.deltaTime;

            // Emergency stop works in every state (명세서 16.7: Esc held abort_hold_s).
            if (kb != null && kb.escapeKey.isPressed)
            {
                _escHeld += Time.unscaledDeltaTime;
                if (!_escFired && _escHeld >= _pc.AbortHoldS) { _escFired = true; Raise(IntentKind.Abort); }
            }
            else { _escHeld = 0; _escFired = false; }

            float rate = (_standing - _pc.CrouchHeadM) / Mathf.Max(_pc.PostureSwitchS, 1e-3f);
            _head = Mathf.MoveTowards(_head, _headTarget, rate * dt);
            ApplyHead();

            if (!InputEnabled || kb == null) { FocusedTargetId = null; return; }

            if (mouse != null)
            {
                var d = mouse.delta.ReadValue() * _pc.MouseSensitivityDeg;
                transform.Rotate(0f, d.x, 0f, UnityEngine.Space.Self);
                _pitch = Mathf.Clamp(_pitch - d.y, -_pc.PitchLimitDeg, _pc.PitchLimitDeg);
                _camera.transform.localRotation = Quaternion.Euler(_pitch, 0, 0);
            }

            var input = new Vector2((kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0),
                                    (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0));
            float speed = _headTarget < _standing ? _pc.CrouchSpeedMps : _pc.WalkSpeedMps;
            var move = transform.TransformDirection(new Vector3(input.x, 0, input.y));
            move = Vector3.ClampMagnitude(move, 1f) * speed;
            _body.Move((move + Vector3.down * _pc.WalkSpeedMps) * dt); // gentle gravity keeps the body on the floor

            if (kb.cKey.wasPressedThisFrame) _headTarget = _headTarget < _standing ? _standing : _pc.CrouchHeadM;

            FocusedTargetId = Raycast();
            if (kb.fKey.wasPressedThisFrame && IsDoor(FocusedTargetId)) Raise(IntentKind.ToggleDoor, FocusedTargetId);
            if (kb.eKey.wasPressedThisFrame && FocusedTargetId != null && !IsDoor(FocusedTargetId))
                Raise(IsPickable(FocusedTargetId) ? IntentKind.Grab : IntentKind.Use, FocusedTargetId);
            if (kb.tKey.wasPressedThisFrame) Raise(IntentKind.TowelToggle);
            if (kb.gKey.wasPressedThisFrame) Raise(IntentKind.Drop);

            if (kb.hKey.isPressed && IsDoor(FocusedTargetId))
            {
                _hHeld += dt;
                if (!_hFired && _hHeld >= _pc.HandleHoldS) { _hFired = true; Raise(IntentKind.HandleCheck, FocusedTargetId, _hHeld); }
            }
            else { _hHeld = 0; _hFired = false; }
        }

        private string Raycast()
        {
            var ray = new Ray(_camera.transform.position, _camera.transform.forward);
            if (!Physics.Raycast(ray, out var hit, _pc.InteractRangeM, ~0, QueryTriggerInteraction.Collide)) return null;
            var target = hit.collider.GetComponentInParent<InteractTarget>();
            return target != null ? target.TargetId : null;
        }

        private bool IsDoor(string id) => id != null && _building.Doors.ContainsKey(id);

        private bool IsPickable(string id) =>
            _building.Objects.TryGetValue(id, out var o) &&
            (o.Kind == ObjectKind.Towel || o.Kind == ObjectKind.Extinguisher || o.Kind == ObjectKind.Phone ||
             o.Kind == ObjectKind.Document || o.Kind == ObjectKind.Bag);

        private void Raise(IntentKind kind, string target = null, float hold = 0f) => IntentRaised?.Invoke(new Intent(kind, target, hold));

        private void ApplyHead()
        {
            float h = Mathf.Max(_head + 0.1f, _body.radius * 2f);
            _body.height = h;
            _body.center = new Vector3(0, h / 2f, 0);
            if (_camera != null) _camera.transform.localPosition = new Vector3(0, _head, 0);
        }
    }
}
