using System.Collections;
using System.Collections.Generic;
using FireDrill.Core;
using FireDrill.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FireDrill.Tests.PlayMode
{
    /// <summary>11장 완료 기준 — NPC: a cache hit answers within 0.5 s; a missing key goes to the server within 3 s;
    /// offline or late it falls back to a spare line with the action unchanged.</summary>
    public class NpcBrainTests
    {
        private static ConfigSet Config() => ConfigSet.Load();

        private static IEnumerator Online(ServerClient client)
        {
            bool? ok = null;
            client.CheckHealth(o => ok = o);
            yield return Harness.WaitFor(() => { client.Update(0f); return ok.HasValue; }, 10f, () => "health");
            Assert.IsTrue(ok.Value, client.LastError);
        }

        private static IEnumerator Decide(NpcBrain brain, ServerClient client, Scenario.NpcSetup npc, NpcAction current,
                                          PlayerAction player, int smoke, System.Action<NpcReaction, NpcSource, float> got)
        {
            bool done = false;
            brain.Decide(npc, current, player, smoke, npc.HomeZone, new List<string>(), (r, s, t) => { got(r, s, t); done = true; });
            yield return Harness.WaitFor(() => { client.Update(0f); return done; }, 10f, () => "no answer");
        }

        [UnityTest]
        public IEnumerator CacheHit_IsImmediate()
        {
            var cfg = Config();
            cfg.App.Offline = true;
            using var client = new ServerClient(cfg.App, cfg.Runtime.Network, 1f);
            var brain = new NpcBrain(cfg.NpcCache, client);
            var minji = cfg.Scenario.Npcs.Find(n => n.Id == "npc_minji");
            NpcSource source = default; float took = -1; NpcReaction r = null;
            yield return Decide(brain, client, minji, NpcAction.Freeze, PlayerAction.Escort, 0, (a, s, t) => { r = a; source = s; took = t; });
            Assert.AreEqual(NpcSource.Cache, source);
            Assert.LessOrEqual(took, 0.5f);
            Assert.AreEqual(cfg.NpcCache.Entries[NpcCache.Key("npc_minji", PlayerAction.Escort, 0, NpcAction.Freeze)].Action, r.Action);
        }

        [UnityTest]
        public IEnumerator CacheMiss_Offline_SpareLineKeepsAction()
        {
            var cfg = Config();
            cfg.App.Offline = true;
            cfg.NpcCache.Entries.Clear();
            using var client = new ServerClient(cfg.App, cfg.Runtime.Network, 1f);
            var brain = new NpcBrain(cfg.NpcCache, client);
            var junho = cfg.Scenario.Npcs.Find(n => n.Id == "npc_junho");
            NpcSource source = default; NpcReaction r = null;
            yield return Decide(brain, client, junho, NpcAction.GoElevator, PlayerAction.Warn, 1, (a, s, t) => { r = a; source = s; });
            Assert.AreEqual(NpcSource.Fallback, source);
            Assert.AreEqual(NpcAction.GoElevator, r.Action);
            CollectionAssert.Contains(cfg.NpcCache.Fallback["npc_junho"], r.Line);
        }

        [UnityTest]
        public IEnumerator CacheMiss_Online_ServerAnswersWithin3s()
        {
            using (var server = new TestServer())
            {
                yield return server.Start();
                var cfg = Config();
                cfg.App.Offline = false;
                cfg.App.ServerUrl = server.Url;
                cfg.NpcCache.Entries.Clear();
                using var client = new ServerClient(cfg.App, cfg.Runtime.Network, 1f);
                yield return Online(client);
                var brain = new NpcBrain(cfg.NpcCache, client);
                var minji = cfg.Scenario.Npcs.Find(n => n.Id == "npc_minji");
                NpcSource source = default; float took = -1; NpcReaction r = null;
                yield return Decide(brain, client, minji, NpcAction.Freeze, PlayerAction.Escort, 2, (a, s, t) => { r = a; source = s; took = t; });
                Assert.AreEqual(NpcSource.Server, source);
                Assert.LessOrEqual(took, cfg.Runtime.Network.NpcTimeoutS);
                Assert.AreEqual(NpcAction.FollowPlayer, r.Action, "fake LLM: escort → follow_player");
            }
        }
    }
}
