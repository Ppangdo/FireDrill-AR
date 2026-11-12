using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using FireDrill.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FireDrill.Tests.PlayMode
{
    /// <summary>Stage 4 gate (명세서 15장): the model and fail play scripts end with the expected outcome and per-item
    /// grades, with the real fire engine, exposure, items and doors. npc_results need NPC agents (stage 6).</summary>
    public class FireDemoTests
    {
        [TearDown]
        public void TearDown() => Harness.Cleanup();

        private static Bootstrap Launch(string script, out ScriptedPlayer player)
        {
            BootstrapOptions.Reset();
            BootstrapOptions.Platform = "scripted";
            BootstrapOptions.Offline = true;
            BootstrapOptions.LogDir = Paths.Out;
            BootstrapOptions.ParticipantCode = "T01";
            BootstrapOptions.PlatformArgs = new Dictionary<string, string> { ["playscript"] = Paths.Playscript(script), ["start"] = "prefire" };
            var boot = Harness.Launch();
            player = (ScriptedPlayer)boot.Platform;
            return boot;
        }

        private static IEnumerator PlayAndCheck(string script)
        {
            var boot = Launch(script, out var player);
            yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Report || player.Error != null, 240f,
                () => $"stuck in {boot.Flow.State} t={boot.Flow.T:0.0} step {player.CurrentStep} head {player.Head} fed {boot.Exposure.Fed:0.00}");
            Assert.IsNull(player.Error, player.Error);
            var expect = player.Script.Expect;
            var ev = boot.LastEvaluation;
            Assert.IsNotNull(ev);
            Assert.IsFalse(boot.Audio.AlarmOn, "alarm stops when the run ends");
            Assert.IsFalse(boot.Audio.CrackleOn, "fire audio stops when the run ends");

            var report = string.Join("\n", ev.Items.Select(i => $"  {i.Id}: {i.Grade.ToString().ToLowerInvariant()} {Newtonsoft.Json.JsonConvert.SerializeObject(i.Evidence)}"));
            Debug.Log($"[{script}] outcome {ev.Outcome} total {ev.TotalPct}% elapsed {ev.ElapsedS:0.0}\n{report}\nlog {boot.LastRunFile}");

            Assert.AreEqual(expect.Outcome, ev.Outcome.ToString().ToLowerInvariant(), $"outcome\n{report}");
            foreach (var kv in expect.Grades)
            {
                var item = ev.Items.Single(i => i.Id == kv.Key);
                Assert.AreEqual(kv.Value, item.Grade.ToString().ToLowerInvariant(), $"{kv.Key}\n{report}");
            }
            var results = boot.Npcs.Results;
            Debug.Log($"[{script}] npc results: {string.Join(", ", results.Select(r => r.Key + "=" + r.Value))}");
            foreach (var kv in expect.NpcResults ?? new Dictionary<string, string>())
                Assert.AreEqual(kv.Value, results[kv.Key].ToString().ToLowerInvariant(), $"npc_results {kv.Key}");
            Assert.AreEqual(boot.Npcs.Results.Count, boot.Report.Current?.NpcResults.Count ?? boot.Npcs.Results.Count, "report lists every NPC");
            Assert.AreEqual(boot.Recorder.Events.Count, Harness.FileLines(boot.LastRunFile).Length);
        }

        [UnityTest]
        public IEnumerator ContinuousSpray_RequiresAimAndExtinguishesBeforeRelease()
        {
            var boot = Launch("walk_play", out var player);
            yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Fire, 20f, () => "no fire");
            var items = boot.Items;
            var fire = items.FireCenter();
            player.Teleport(new Vec3(fire.x, 0f, fire.z), 0f);
            items.Inventory.Pickup("o_ext_corr_s");
            float duration = boot.Config.Fire.ExtinguishNeededS;
            items.UpdateSpray(true, false, duration);
            Assert.AreEqual(0f, items.ExtinguishProgress, "spraying away from the fire only spends charge");
            Assert.AreEqual(FirePhase.Initial, boot.Fire.Engine.Phase);

            items.UpdateSpray(true, true, duration);
            Assert.AreEqual(FirePhase.Extinguished, boot.Fire.Engine.Phase);
            Assert.IsTrue(items.IsSpraying);
            Assert.IsTrue(boot.Recorder.Events.Any(e => e.Type == EventTypes.Extinguish && (bool)e.Data["success"]));

            items.UpdateSpray(false, false, 0f);
            Assert.IsFalse(items.IsSpraying);
        }

        [UnityTest]
        public IEnumerator ModelPlay_MatchesExpectedGrades() => PlayAndCheck("model_play");

        [UnityTest]
        public IEnumerator FailPlay_MatchesExpectedGrades() => PlayAndCheck("fail_play");

        /// <summary>Stage 6 gate (명세서 15장), second half: Junho, whom the trainee does not warn, stays in the elevator
        /// lobby (waiting at the button, or collapsed there). The first half — Minji follows out — is model_play's
        /// npc_results, checked in ModelPlay_MatchesExpectedGrades.</summary>
        [UnityTest]
        public IEnumerator Npcs_JunhoNotWarned_StaysAtElevator()
        {
            var boot = Launch("fail_play", out var player);
            yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Report || player.Error != null, 240f,
                () => $"stuck in {boot.Flow.State} t={boot.Flow.T:0.0} step {player.CurrentStep}");
            var junho = boot.Npcs.Agents["npc_junho"];
            Assert.AreEqual("z_elev_lobby", junho.Zone);
            Assert.AreNotEqual(NpcResult.Rescued, boot.Npcs.Results["npc_junho"]);
            Assert.AreEqual(NpcAction.GoElevator, junho.Action);
            Assert.AreNotEqual(NpcResult.Rescued, boot.Npcs.Results["npc_minji"], "left frozen in the lab");
        }

        [UnityTest]
        public IEnumerator Abort_TurnsEffectsOff()
        {
            var boot = Launch("fail_play", out var player);
            yield return Harness.WaitFor(() => boot.Flow.State == FlowState.Fire && boot.Flow.T - boot.Flow.FireT > 50f, 120f,
                () => $"state {boot.Flow.State} t {boot.Flow.T:0.0}");
            Assert.IsTrue(boot.Smoke.SmokeMaterials.Any(), "smoke built");
            boot.Flow.Abort("staff");
            yield return null;
            Assert.AreEqual(FlowState.Aborted, boot.Flow.State);
            Assert.IsFalse(RenderSettings.fog);
            Assert.IsFalse(boot.Audio.AlarmOn);
            Assert.IsFalse(boot.Audio.CrackleOn);
            Assert.IsFalse(boot.Smoke.GetComponentsInChildren<MeshRenderer>().Any(r => r.enabled && r.name.StartsWith("Smoke_")));
        }
    }
}
