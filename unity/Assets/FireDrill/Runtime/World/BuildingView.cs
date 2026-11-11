using System.Collections.Generic;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>What SceneBuilder made, by id. Presentation only: state lives in GameFlow / Interactables.</summary>
    public sealed class BuildingView : MonoBehaviour
    {
        public readonly Dictionary<string, ZoneView> Zones = new Dictionary<string, ZoneView>();
        public readonly Dictionary<string, DoorView> Doors = new Dictionary<string, DoorView>();
        public readonly Dictionary<string, ObjectView> Objects = new Dictionary<string, ObjectView>();
        public readonly List<GameObject> Walls = new List<GameObject>();
        public readonly List<GameObject> ExitSigns = new List<GameObject>();
    }

    public sealed class ZoneView
    {
        public readonly string Id;
        public readonly GameObject Floor, Ceiling;
        public readonly Light Light;
        public ZoneView(string id, GameObject floor, GameObject ceiling, Light light)
        {
            Id = id; Floor = floor; Ceiling = ceiling; Light = light;
        }
    }

    public sealed class ObjectView
    {
        public readonly string Id;
        public readonly ObjectKind Kind;
        public readonly GameObject Go;
        public ObjectView(string id, ObjectKind kind, GameObject go) { Id = id; Kind = kind; Go = go; }
    }

    /// <summary>Marks a collider as an interaction target (object o_, door e_, NPC npc_) for raycasts.</summary>
    public sealed class InteractTarget : MonoBehaviour
    {
        public string TargetId;
    }

    /// <summary>A door leaf on a hinge: swings 90° into its "from" zone in door_swing_s. Blocks only when closed.</summary>
    public sealed class DoorView : MonoBehaviour
    {
        private Quaternion _closed, _open;
        private float _swingS;
        private Collider _collider;
        private float _progress; // 0 closed .. 1 open

        public string EdgeId { get; private set; }
        public bool IsOpen { get; private set; }
        public bool Moving => IsOpen ? _progress < 1f : _progress > 0f;

        public void Init(string edgeId, Quaternion closed, Quaternion open, float swingS, Collider collider)
        {
            EdgeId = edgeId;
            _closed = closed;
            _open = open;
            _swingS = swingS;
            _collider = collider;
        }

        public void SetOpen(bool open, bool instant = false)
        {
            IsOpen = open;
            if (_collider != null) _collider.enabled = !open;
            if (instant || _swingS <= 0) Apply(open ? 1f : 0f);
        }

        private void Update()
        {
            float target = IsOpen ? 1f : 0f;
            if (Mathf.Approximately(_progress, target)) return;
            Apply(Mathf.MoveTowards(_progress, target, Time.deltaTime / _swingS));
        }

        private void Apply(float p)
        {
            _progress = p;
            transform.rotation = Quaternion.Slerp(_closed, _open, Mathf.SmoothStep(0, 1, p));
        }
    }
}
