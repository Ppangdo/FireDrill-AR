using System.Text;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>Stage 3 UI stand-in (명세서 15장 "UI: 디버그 텍스트"): state, position, server status, start and
    /// report buttons. English text because the Korean font (16.1) arrives with the UiRoot in stage 7.</summary>
    public sealed class DebugOverlay : MonoBehaviour
    {
        private Bootstrap _boot;
        private string _code = "P00";
        private GUIStyle _box;

        public void Init(Bootstrap boot) => _boot = boot;

        private void OnGUI()
        {
            if (_boot == null) return;
            _box ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = true };

            if (_boot.ConfigError != null)
            {
                GUI.Box(new Rect(20, 20, Screen.width - 40, 160), "CONFIG ERROR — cannot start\n\n" + _boot.ConfigError, _box);
                return;
            }
            var flow = _boot.Flow;
            if (flow == null) return;

            var sb = new StringBuilder();
            sb.AppendLine($"State {flow.State}{(flow.Paused ? "  [PAUSED: server silent]" : "")}   run t {flow.T:0.0}s   attempt {flow.Attempt}");
            var head = _boot.Platform.Rig.Head;
            var zone = _boot.Config.Graph.ZoneAt(head);
            sb.AppendLine($"Zone {zone ?? "-"}   head [{head.X:0.00}, {head.Y:0.00}, {head.Z:0.00}]   ratio {head.Y / _boot.Platform.Rig.StandingHeight:0.00}");
            var s = _boot.Server;
            sb.AppendLine($"Server {s.Mode}{(s.WsConnected ? " +ws" : "")}   uploaded {s.Uploaded}   pending {s.Pending}{(s.LastError != null && s.Mode != ServerMode.Online ? "   (" + s.LastError + ")" : "")}");
            if (_boot.Recorder.Recording) sb.AppendLine($"Run {flow.RunId}   events {_boot.Recorder.Events.Count}");
            var focus = _boot.Platform.Input.FocusedTargetId;
            if (focus != null) sb.AppendLine("Target " + focus + (focus.StartsWith("e_") ? "   F: open/close   H (hold): check handle" : ""));
            if (_boot.Doors.LastRefusal != null) sb.AppendLine("Last refused: " + _boot.Doors.LastRefusal);
            sb.Append("WASD move · mouse look · C crouch · F door · H handle · Esc (hold) abort");
            GUI.Box(new Rect(10, 10, 640, 130), sb.ToString(), _box);

            if (_boot.WaitingForStart && flow.State == FlowState.Calibration)
            {
                GUILayout.BeginArea(new Rect(Screen.width / 2f - 160, Screen.height / 2f - 60, 320, 120), GUI.skin.box);
                GUILayout.Label("Participant code (no real names)");
                _code = GUILayout.TextField(_code, 16);
                if (GUILayout.Button("Start training") && _code.Trim().Length > 0) _boot.Begin(_code.Trim());
                GUILayout.EndArea();
            }

            if (flow.State == FlowState.Ending || flow.State == FlowState.Report || flow.State == FlowState.Aborted)
            {
                var ev = _boot.LastEvaluation;
                var text = $"Outcome {flow.LastOutcome?.ToString() ?? "-"}";
                if (ev != null) text += $"   score {ev.TotalPct}%   elapsed {ev.ElapsedS:0.0}s";
                text += $"\nLog: {_boot.LastRunFile}";
                GUILayout.BeginArea(new Rect(Screen.width / 2f - 260, Screen.height / 2f - 70, 520, 140), GUI.skin.box);
                GUILayout.Label(text);
                if (flow.State == FlowState.Report && GUILayout.Button("Retry (new run)")) flow.Retry();
                if ((flow.State == FlowState.Report || flow.State == FlowState.Aborted) && GUILayout.Button("Quit")) Quit();
                GUILayout.EndArea();
            }
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
