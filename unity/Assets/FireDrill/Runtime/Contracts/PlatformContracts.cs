using System;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>The trainee body. PC: character controller. Quest: headset. ScriptedPlayer: tests (명세서 14.3, 15.2).</summary>
    public interface IPlayerRig
    {
        /// <summary>Head position in building coordinates.</summary>
        Vec3 Head { get; }
        Vec3 Forward { get; }
        /// <summary>Standing head height measured at calibration (PC: 1.65 fixed).</summary>
        float StandingHeight { get; }
        /// <summary>Retry spawn. Quest shows guidance only.</summary>
        void Teleport(Vec3 feet, float yawDeg);
    }

    public enum IntentKind { Use, Grab, Drop, HandleCheck, ToggleDoor, TalkTo, TowelToggle, Abort }

    /// <summary>A resolved player intention: target already chosen by the platform adapter.</summary>
    public readonly struct Intent
    {
        public readonly IntentKind Kind;
        /// <summary>Object (o_), edge (e_) or NPC (npc_) id; null for TowelToggle/Abort/Drop.</summary>
        public readonly string TargetId;
        /// <summary>How long the input was held (s): faucet 2 s, handle check 0.5 s.</summary>
        public readonly float HoldSeconds;

        public Intent(IntentKind kind, string targetId = null, float holdSeconds = 0f)
        {
            Kind = kind; TargetId = targetId; HoldSeconds = holdSeconds;
        }
    }

    /// <summary>PC: keyboard + centre raycast. Quest: hand tracking. Tests: ScriptedPlayer.</summary>
    public interface IInteractionSource
    {
        event Action<Intent> IntentRaised;
        /// <summary>Target under the crosshair/hand for the interaction prompt, or null.</summary>
        string FocusedTargetId { get; }
    }

    /// <summary>Maps building coordinates to world coordinates. PC: identity, always ready.</summary>
    public interface ISpaceProvider
    {
        bool Ready { get; }
        Matrix4x4 BuildingToWorld { get; }
    }

    /// <summary>Game flow states (명세서 5장).</summary>
    public enum FlowState { Boot, Calibration, Tutorial, PreFire, Fire, Ending, Report, Aborted }
}
