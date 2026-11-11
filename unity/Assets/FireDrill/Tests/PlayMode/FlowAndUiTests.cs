using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FireDrill.Core;
using FireDrill.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace FireDrill.Tests.PlayMode
{
    /// <summary>Stage 7 gate (명세서 15장): the scripted player goes from Boot to Report without a break (tutorial
    /// dialogue, errand, fire, escape, ending, report), and an emergency stop reaches Aborted within 1 s. Also the
    /// 16.11 screens appear in the right states; screenshots go to out/screenshots/stage7.</summary>
    public class FlowAndUiTests
    {
        [TearDown]
        public void TearDown() => Harness.Cleanup();

        private static Bootstrap Launch(string script, string participant, out ScriptedPlayer player)
        {
            BootstrapOptions.Reset();
            BootstrapOptions.Platform = "scripted";
            BootstrapOptions.Offline = true;
            BootstrapOptions.LogDir = Paths.Out;
            BootstrapOptions.ParticipantCode = participant;
            BootstrapOptions.PlatformArgs = new Dictionary<string, string> { ["playscript"] = Paths.Playscript(script), ["start"] = "tutorial" };
            var boot = Harness.Launch();
            player = (ScriptedPlayer)boot.Platform;
            return boot;
        }

        [UnityTest]
        public IEnumerator BootToReport_WithoutABreak()
        {
            var boot = Launch("tutorial_play", "T07", out var player);
            var states = new List<FlowState>();
            float preFireAt = -1f;
            boot.Flow.StateChanged += (from, to) => { states.Add(to); if (to == FlowState.PreFire) preFireAt = boot.Flow.T; };
            bool sawIntro = false, sawReturn = false, sawEnding = false, sawHud = false, shotDialogue = false, shotEnding = false;
            string firstLine = null;

            float start = Time.realtimeSinceStartup;
            while (boot.Flow.State != FlowState.Report && player.Error == null)
            {
                if (Time.realtimeSinceStartup - start > 180f) Assert.Fail($"stuck in {boot.Flow.State} step {player.CurrentStep} t={boot.Flow.T:0.0}");
                if (boot.Ui.DialogueShown && boot.Dialogue.NodeId == "intro_1") { sawIntro = true; firstLine = boot.Ui.DialogueText; }
                if (boot.Ui.DialogueShown && boot.Dialogue.NodeId == "errand_return") sawReturn = true;
                if (boot.Flow.State == FlowState.Ending && boot.Ui.EndingShown) sawEnding = true;
                if (boot.Flow.State == FlowState.Fire && boot.Ui.HudShown) sawHud = true;
                if (boot.Flow.State == FlowState.Tutorial && sawIntro && boot.Ui.HudShown && !shotDialogue)
                {
                    shotDialogue = true;
                    yield return Shot(boot, "2_tutorial_dialogue");
                }
                if (boot.Flow.State == FlowState.Ending && !shotEnding)
                {
                    shotEnding = true;
                    yield return Shot(boot, "3_ending");
                }
                yield return null;
            }
            Assert.IsNull(player.Error, player.Error);
            CollectionAssert.AreEqual(new[] { FlowState.Calibration, FlowState.Tutorial, FlowState.PreFire, FlowState.Fire, FlowState.Ending, FlowState.Report },
                                      states.ToArray(), "Boot → Report in order");
            Assert.IsTrue(sawIntro, "tutorial dialogue shown");
            Assert.AreEqual(boot.Config.Dialogue.Nodes["intro_1"].Text, firstLine);
            Assert.IsTrue(sawReturn, "errand return dialogue shown");
            Assert.IsTrue(sawHud, "HUD during the fire");
            Assert.IsTrue(sawEnding, "ending screen");
            Assert.That(preFireAt, Is.GreaterThan(0f).And.LessThan(boot.Config.Scenario.Tutorial.MaxS),
                        "the errand (return dialogue → to_prefire) ended the tutorial, not the time limit");
            Assert.AreEqual(Outcome.Escaped, boot.LastEvaluation.Outcome);
            Assert.IsTrue(boot.Recorder.Events.Any(e => e.Type == "PICKUP" && (string)e.Data["object"] == "o_document"), "errand object picked");
            yield return null;
            Assert.IsTrue(boot.ReportScreen.Visible);
            Assert.IsFalse(boot.Ui.HudShown || boot.Ui.DialogueShown, "report alone on screen");
        }

        [UnityTest]
        public IEnumerator StartScreen_WaitsForStaffConfirm()
        {
            var boot = Launch("tutorial_play", null, out _);
            yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Calibration, 10f, () => "no calibration");
            yield return null;
            Assert.IsTrue(boot.Ui.StartShown, "briefing / start screen");
            Assert.IsTrue(boot.WaitingForStart);
            yield return Shot(boot, "1_start");
            for (int i = 0; i < 30; i++) yield return null;
            Assert.AreEqual(FlowState.Calibration, boot.Flow.State, "nothing starts without the confirm button");
            boot.Begin("T08");
            yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Tutorial, 5f, () => "did not start");
            yield return null;
            Assert.IsFalse(boot.Ui.StartShown);
            Assert.IsTrue(boot.Ui.DialogueShown);
        }

        [UnityTest]
        public IEnumerator EscAbort_InTutorial_AbortedWithinOneSecond()
        {
            var boot = Launch("tutorial_play", "T09", out var player);
            yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Tutorial, 10f, () => "no tutorial");
            float t = Time.realtimeSinceStartup;
            player.Raise(new Intent(IntentKind.Abort)); // PC: Esc held abort_hold_s
            yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Aborted && boot.Ui.AbortedShown, 1f, () => "not aborted within 1 s");
            Assert.LessOrEqual(Time.realtimeSinceStartup - t, 1f);
            Assert.AreEqual("aborted", (string)boot.Recorder.Events.Last().Data["outcome"]);
            Assert.IsFalse(boot.Ui.DialogueShown || boot.Ui.HudShown);
            yield return Shot(boot, "4_aborted");
        }

        private static string ShotPath(string name) => Path.Combine(Paths.Repo, "out", "screenshots", "stage7", name + ".png");

        private static IEnumerator Shot(Bootstrap boot, string name)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) yield break;
            var rt = new RenderTexture(1920, 1080, 24);
            boot.Ui.SetTargetTexture(rt);
            for (int i = 0; i < 3; i++) yield return null;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            Directory.CreateDirectory(Path.GetDirectoryName(ShotPath(name)));
            File.WriteAllBytes(ShotPath(name), tex.EncodeToPNG());
            Object.Destroy(tex);
            boot.Ui.SetTargetTexture(null);
            rt.Release();
        }
    }
}
