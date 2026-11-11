using System.Text;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>UI stand-in until UiRoot (stage 7): state, server status, start / report buttons, subtitles, inventory,
    /// NPC choices. Labels are English; subtitles and dialogue are the Korean config text (16.11) and use the
    /// system font fallback until NotoSansKR arrives. No FED or health gauge during the fire (16.11).</summary>
    public sealed class DebugOverlay : MonoBehaviour
    {
        private Bootstrap _boot;
        private string _code = "P00";
        private GUIStyle _box, _subtitle, _small;

        public void Init(Bootstrap boot) => _boot = boot;

        private void OnGUI()
        {
            if (_boot == null) return;
            _box ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = true };
            _subtitle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 22, wordWrap = true };
            _small ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerRight, fontSize = 14 };

            if (_boot.ConfigError != null)
            {
                GUI.Box(new Rect(20, 20, Screen.width - 40, 160), "CONFIG ERROR — cannot start\n\n" + _boot.ConfigError, _box);
                return;
            }
            var flow = _boot.Flow;
            if (flow == null) return;
            if (_boot.ReportScreen != null && _boot.ReportScreen.Visible) return; // the report screen has its own buttons

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
            var refusal = _boot.Items.LastRefusal ?? _boot.Doors.LastRefusal ?? _boot.Npcs.LastRefusal;
            if (refusal != null) sb.AppendLine("Last refused: " + refusal);
            sb.Append("WASD · mouse · C crouch · E use/talk · F door · H handle · T towel · G drop · LMB spray · Esc (hold) abort");
            GUI.Box(new Rect(10, 10, 700, 150), sb.ToString(), _box);

            if (_boot.Subtitle != null)
                GUI.Label(new Rect(Screen.width * 0.1f, Screen.height - 120, Screen.width * 0.8f, 40), _boot.Subtitle, _subtitle);

            var inv = _boot.Items.Inventory;
            string Name(string id) => id == null ? "-" : _boot.Items.KindOf(id) ?? id;
            var bag = inv.Bag;
            var towel = !inv.HasTowel ? "no towel" : (inv.TowelWet ? "wet" : "dry") + (inv.TowelEquipped ? ", on face" : "");
            GUI.Label(new Rect(Screen.width - 330, Screen.height - 100, 320, 90),
                      $"hand: {Name(inv.Hand)}\nbag 1: {Name(bag.Count > 0 ? bag[0] : null)}\nbag 2: {Name(bag.Count > 1 ? bag[1] : null)}\ntowel: {towel}", _small);

            if (_boot.Npcs.Talking != null)
            {
                var node = _boot.Npcs.Node;
                var lines = new StringBuilder();
                lines.AppendLine($"{node?.Speaker ?? _boot.Npcs.TalkingName}: {node?.Text}");
                var choices = _boot.Npcs.Choices;
                for (int i = 0; i < choices.Count; i++) lines.AppendLine($"{i + 1}. {choices[i].Text}");
                GUI.Box(new Rect(Screen.width * 0.15f, Screen.height * 0.62f, Screen.width * 0.7f, 30 + 24 * choices.Count), lines.ToString(), _box);
            }

            if (_boot.WaitingForStart && flow.State == FlowState.Calibration)
            {
                GUILayout.BeginArea(new Rect(Screen.width / 2f - 160, Screen.height / 2f - 60, 320, 120), GUI.skin.box);
                GUILayout.Label("Participant code (no real names)");
                _code = GUILayout.TextField(_code, 16);
                if (GUILayout.Button("Start training") && _code.Trim().Length > 0) _boot.Begin(_code.Trim());
                GUILayout.EndArea();
            }

            bool reportShown = _boot.ReportScreen != null && _boot.ReportScreen.Visible;
            if (!reportShown && (flow.State == FlowState.Ending || flow.State == FlowState.Report || flow.State == FlowState.Aborted))
            {
                var ev = _boot.LastEvaluation;
                var text = $"Outcome {flow.LastOutcome?.ToString() ?? "-"}";
                if (ev != null) text += $"   score {ev.TotalPct}%   elapsed {ev.ElapsedS:0.0}s";
                text += $"\nSmoke at your head when it ended: c = {_boot.Exposure.CHead:0.00}   FED {_boot.Exposure.Fed:0.00}";
                text += $"\nLog: {_boot.LastRunFile}";
                GUILayout.BeginArea(new Rect(Screen.width / 2f - 260, Screen.height / 2f - 70, 520, 140), GUI.skin.box);
                GUILayout.Label(text);
                if (flow.State == FlowState.Report && GUILayout.Button("Retry (new run)")) flow.Retry();
                if ((flow.State == FlowState.Report || flow.State == FlowState.Aborted) && GUILayout.Button("Quit")) Bootstrap.Quit();
                GUILayout.EndArea();
            }
        }
    }
}
