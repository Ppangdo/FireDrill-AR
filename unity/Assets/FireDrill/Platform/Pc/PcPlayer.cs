using System;
using FireDrill.Core;
using FireDrill.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FireDrill.Platform.Pc
{
    /// <summary>PC trainee (명세서 10장, 16.7): CharacterController body, first-person camera, keyboard and mouse.
    /// Raises intents for the target under the screen centre; range and rules are checked by Interactables.</summary>
    public sealed class PcPlayer : MonoBehaviour, IPlatform, IPlayerRig, IInteractionSource, IHeldUseFeedback, IContinuousSprayInput
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
        private ZoneGraph _graph;
        private string _handleTarget, _useTarget;
        private CharacterController _body;
        private Camera _camera;
        private float _standing, _head, _headTarget, _pitch;
        private float _hHeld, _escHeld, _eHeld;
        private bool _hFired, _escFired, _eFired;
        private float _shakeDeg, _shakeHz;
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
        public bool SprayHeld { get; private set; }
        public float HeldUseSeconds => !_eFired && IsKind(_useTarget, ObjectKind.Faucet) ? _eHeld : 0f;
        public event Action<int> ChoiceKey;
        public event Action DebugToggle;
        private bool _uiFocus = true;

        public bool UiFocus
        {
            get => _uiFocus;
            set
            {
                if (_uiFocus == value) return;
                _uiFocus = value;
                ApplyCursor(value);
            }
        }

        public void SetShake(float amplitudeDeg, float hz)
        {
            _shakeDeg = amplitudeDeg;
            _shakeHz = hz;
        }

        public bool InputEnabled
        {
            get => _inputEnabled;
            set => _inputEnabled = value;
        }

        private static void ApplyCursor(bool show)
        {
            Cursor.lockState = show ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = show;
        }

        private void LateUpdate()
        {
            if (_uiFocus && (Cursor.lockState != CursorLockMode.None || !Cursor.visible)) ApplyCursor(true);
        }

        private void OnDisable() => ApplyCursor(true);

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
            _graph = ctx.Config.Graph;
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
            ApplyCursor(true);
        }

        // ---- per frame ---------------------------------------------------------

        private void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            float dt = Time.deltaTime;

            // Emergency stop works in every state (명세서 16.7: Esc held abort_hold_s).
            if (kb != null && kb.f1Key.wasPressedThisFrame) DebugToggle?.Invoke();
            if (kb != null && kb.escapeKey.isPressed)
            {
                _escHeld += Time.unscaledDeltaTime;
                if (!_escFired && _escHeld >= _pc.AbortHoldS) { _escFired = true; Raise(IntentKind.Abort); }
            }
            else { _escHeld = 0; _escFired = false; }

            float rate = (_standing - _pc.CrouchHeadM) / Mathf.Max(_pc.PostureSwitchS, 1e-3f);
            _head = Mathf.MoveTowards(_head, _headTarget, rate * dt);
            ApplyHead();

            if (!InputEnabled || kb == null)
            {
                FocusedTargetId = null;
                SprayHeld = false;
                _handleTarget = _useTarget = null;
                _eHeld = 0f;
                _eFired = kb != null && kb.eKey.isPressed;
                _hHeld = 0f;
                _hFired = false;
                return;
            }

            bool captureClick = mouse != null && !_uiFocus &&
                Cursor.lockState != CursorLockMode.Locked && mouse.leftButton.wasPressedThisFrame;
            if (captureClick) ApplyCursor(false);
            if (mouse != null && !_uiFocus && Cursor.lockState == CursorLockMode.Locked && !captureClick)
            {
                var d = mouse.delta.ReadValue() * _pc.MouseSensitivityDeg;
                transform.Rotate(0f, d.x, 0f, UnityEngine.Space.Self);
                _pitch = Mathf.Clamp(_pitch - d.y, -_pc.PitchLimitDeg, _pc.PitchLimitDeg);
                float shakeX = 0f, shakeY = 0f;
                if (_shakeDeg > 0f)
                {
                    shakeX = (Mathf.PerlinNoise(Time.time * _shakeHz, 0.1f) - 0.5f) * 2f * _shakeDeg;
                    shakeY = (Mathf.PerlinNoise(0.7f, Time.time * _shakeHz) - 0.5f) * 2f * _shakeDeg;
                }
                _camera.transform.localRotation = Quaternion.Euler(_pitch + shakeX, shakeY, 0);
            }

            var input = new Vector2((kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0),
                                    (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0));
            float speed = _headTarget < _standing ? _pc.CrouchSpeedMps : _pc.WalkSpeedMps;
            var move = transform.TransformDirection(new Vector3(input.x, 0, input.y));
            move = Vector3.ClampMagnitude(move, 1f) * speed;
            _body.Move((move + Vector3.down * _pc.WalkSpeedMps) * dt); // gentle gravity keeps the body on the floor

            if (kb.cKey.wasPressedThisFrame) _headTarget = _headTarget < _standing ? _standing : _pc.CrouchHeadM;

            FocusedTargetId = Raycast();
            var nearbyHandle = FindHandle(FocusedTargetId);
            FocusedTargetId ??= nearbyHandle;
            if (kb.fKey.wasPressedThisFrame && IsDoor(FocusedTargetId)) Raise(IntentKind.ToggleDoor, FocusedTargetId);
            UpdateUse(kb.eKey.isPressed, dt);

            SprayHeld = mouse != null && mouse.leftButton.isPressed && !_uiFocus &&
                Cursor.lockState == CursorLockMode.Locked && !captureClick;

            if (kb.digit1Key.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame) ChoiceKey?.Invoke(0);
            if (kb.digit2Key.wasPressedThisFrame) ChoiceKey?.Invoke(1);
            if (kb.digit3Key.wasPressedThisFrame) ChoiceKey?.Invoke(2);
            if (kb.tKey.wasPressedThisFrame) Raise(IntentKind.TowelToggle);
            if (kb.gKey.wasPressedThisFrame) Raise(IntentKind.Drop);
            if (kb.zKey.wasPressedThisFrame) Raise(IntentKind.SwapBag1);
            if (kb.xKey.wasPressedThisFrame) Raise(IntentKind.SwapBag2);

            if (!kb.hKey.isPressed || _uiFocus)
            {
                _handleTarget = null;
                _hHeld = 0f;
                _hFired = false;
            }
            else if (!_hFired)
            {
                if (_handleTarget != nearbyHandle)
                {
                    _handleTarget = nearbyHandle;
                    _hHeld = 0f;
                }
                if (_handleTarget != null)
                {
                    _hHeld += dt;
                    if (_hHeld >= _pc.HandleHoldS)
                    {
                        _hFired = true;
                        Raise(IntentKind.HandleCheck, _handleTarget, _hHeld);
                    }
                }
            }
        }

        private void UpdateUse(bool pressed, float dt)
        {
            if (!pressed || _uiFocus)
            {
                _useTarget = null;
                _eHeld = 0f;
                _eFired = pressed;
                return;
            }

            if (_eFired) return;
            if (IsKind(_useTarget, ObjectKind.Faucet))
            {
                // Keep the selected faucet through small camera movements while it is reachable.
                if (!CanContinueFaucetUse(_useTarget))
                {
                    _useTarget = null;
                    _eHeld = 0f;
                    _eFired = true;
                    return;
                }
                FocusedTargetId = _useTarget;
            }
            else
            {
                _useTarget = FocusedTargetId;
                if (_useTarget == null || IsDoor(_useTarget) || _useTarget == ItemInteractions.FireTarget) return;
            }

            if (IsKind(_useTarget, ObjectKind.Faucet))
            {
                _eHeld += dt;
                if (_eHeld < _pc.FaucetHoldS) return;
                _eFired = true;
                Raise(IntentKind.Use, _useTarget, _eHeld);
                return;
            }

            _eFired = true;
            if (_useTarget.StartsWith("npc_")) Raise(IntentKind.TalkTo, _useTarget);
            else Raise(IsPickable(_useTarget) ? IntentKind.Grab : IntentKind.Use, _useTarget);
        }

        private bool CanContinueFaucetUse(string id)
        {
            if (!_building.Objects.TryGetValue(id, out var item) || !item.Go.activeInHierarchy ||
                !item.Go.TryGetComponent<Collider>(out var collider)) return false;
            var origin = _camera.transform.position;
            var centerOffset = collider.bounds.center - origin;
            return Vector3.Dot(_camera.transform.forward, centerOffset) > 0f &&
                CanSeeItem(origin, centerOffset, collider, _pc.InteractRangeM);
        }

        private string FindHandle(string focusedId)
        {
            var head = Head;
            var zone = _graph.ZoneAt(head);
            if (zone == null) return null;

            string nearest = null;
            float bestDistance = float.MaxValue;
            var forward = _camera.transform.forward;
            foreach (var edge in _graph.EdgesOf(zone))
            {
                if (edge.Type != EdgeType.Door) continue;
                var handle = edge.Handle ?? new Vec3(edge.Center.X, 1f, edge.Center.Z);
                var distance = Vec2.Distance(head.Xz, handle.Xz);
                if (distance > _pc.HandleRangeM) continue;
                if (edge.Id == focusedId) return edge.Id;

                var toDoor = new Vector3(edge.Center.X - head.X, 0f, edge.Center.Z - head.Z);
                if (Vector3.Dot(forward, toDoor) <= 0f || distance >= bestDistance) continue;
                nearest = edge.Id;
                bestDistance = distance;
            }
            return nearest;
        }

        private string Raycast()
        {
            var center = _camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            var ray = new Ray(_camera.transform.position, center.direction);
            float range = _pc.InteractRangeM;
            // Item interactions measure reach along the floor. Allow the sight ray to reach
            // a low object within that range even when looking down from standing height.
            float sightRange = Mathf.Sqrt(range * range + ray.origin.y * ray.origin.y);
            if (Physics.Raycast(ray, out var hit, sightRange, ~0, QueryTriggerInteraction.Collide))
            {
                var target = hit.collider.GetComponentInParent<InteractTarget>();
                if (target != null)
                {
                    bool itemInReach = _building.Objects.ContainsKey(target.TargetId) &&
                        InHorizontalReach(ray.origin, hit.point, range);
                    if (hit.distance <= range || itemInReach) return target.TargetId;
                }
            }

            // The direct target wins. For thin objects, look for a visible surface
            // within one body width of the crosshair's ray.
            string nearest = null;
            float bestOffset = _body.radius * 2f;
            foreach (var item in _building.Objects.Values)
            {
                if (!item.Go.activeInHierarchy || !item.Go.TryGetComponent<Collider>(out var collider)) continue;
                var centerOffset = collider.bounds.center - ray.origin;
                float projection = Vector3.Dot(centerOffset, ray.direction);
                if (projection <= 0f) continue;

                Vector3 surface = collider.ClosestPoint(ray.GetPoint(Mathf.Min(projection, sightRange)));
                if (!InHorizontalReach(ray.origin, surface, range)) continue;

                Vector3 surfaceOffset = surface - ray.origin;
                float offset = Vector3.Cross(ray.direction, surfaceOffset).magnitude;
                if (offset >= bestOffset) continue;
                if (!CanSeeItem(ray.origin, surfaceOffset, collider, range) &&
                    !CanSeeItem(ray.origin, centerOffset, collider, range)) continue;

                bestOffset = offset;
                nearest = item.Id;
            }
            return nearest;
        }

        private static bool CanSeeItem(Vector3 origin, Vector3 toward, Collider collider, float range)
        {
            // Extend to the collider's far side so a ray aimed at its boundary can register contact.
            float distance = toward.magnitude + collider.bounds.extents.magnitude;
            return Physics.Raycast(origin, toward.normalized, out var hit, distance, ~0, QueryTriggerInteraction.Collide) &&
                hit.collider == collider && InHorizontalReach(origin, hit.point, range);
        }

        private static bool InHorizontalReach(Vector3 origin, Vector3 point, float range)
        {
            var delta = point - origin;
            delta.y = 0f;
            return delta.sqrMagnitude <= range * range;
        }

        private bool IsDoor(string id) => id != null && _building.Doors.ContainsKey(id);

        private bool IsKind(string id, ObjectKind kind) => id != null && _building.Objects.TryGetValue(id, out var o) && o.Kind == kind;

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
