using System;
using System.Collections.Generic;
using FireDrill.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FireDrill.Tests
{
    public class EventLogTests
    {
        static readonly Snapshot Snap = new Snapshot(12.5f, "z_corr_m", new Vec3(1, 1.6f, 9), 0.55f, 0.2f, 0.1f, 0.4f, 1.7f);

        [Test]
        public void Seq_StartsAtOne_AndAppendedFiresInOrder()
        {
            var log = new EventLog("run-1");
            var seen = new List<int>();
            log.Appended += e => seen.Add(e.Seq);
            log.Append(EventTypes.RunStart, Snap, new { scenario_id = "s_demo_fire", attempt = 1 });
            log.Append(EventTypes.Sample, Snap);
            var last = log.Append(EventTypes.AlarmPressed, Snap, new { @object = "o_alarm_corr_m" });

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, seen);
            Assert.AreEqual(3, log.Events.Count);
            Assert.AreSame(last, log.Events[2]);
            Assert.AreEqual("run-1", last.RunId);
            Assert.AreEqual(Snap.T, last.T);
            Assert.AreEqual(Snap.Zone, last.Zone);
            Assert.AreEqual(Snap.Pos, last.Pos);
            Assert.AreEqual(Snap.LayerY, last.LayerY);
            Assert.AreEqual("o_alarm_corr_m", (string)last.Data["object"]);
            Assert.AreEqual(0, log.Events[1].Data.Count);
        }

        [Test]
        public void Data_AnonymousObject_IsSnakeCase()
        {
            var log = new EventLog("r");
            var e = log.Append(EventTypes.DoorOpen, Snap, new { edge = "e_302_door", hot = false, checkedBefore = true });
            Assert.AreEqual(true, (bool)e.Data["checked_before"]);
            Assert.AreEqual("e_302_door", (string)e.Data["edge"]);
        }

        [Test]
        public void Data_Dictionary_KeepsKeys()
        {
            var log = new EventLog("r");
            var e = log.Append(EventTypes.NpcInteract, Snap, new Dictionary<string, object> { ["npc"] = "npc_minji", ["action"] = "warn" });
            Assert.AreEqual("npc_minji", (string)e.Data["npc"]);
            Assert.AreEqual("warn", (string)e.Data["action"]);
        }

        [Test]
        public void Data_JObject_IsCopied()
        {
            var log = new EventLog("r");
            var data = new JObject { ["stage"] = 2 };
            var e = log.Append(EventTypes.BodyStage, Snap, data);
            data["stage"] = 3;
            Assert.AreEqual(2, (int)e.Data["stage"]);
        }

        [Test]
        public void Data_Enums_AreSnakeCaseStrings()
        {
            var log = new EventLog("r");
            var e = log.Append(EventTypes.RunEnd, Snap, new { outcome = Outcome.Escaped });
            Assert.AreEqual("escaped", (string)e.Data["outcome"]);
        }

        [Test]
        public void Data_MustBeAnObject()
        {
            var log = new EventLog("r");
            Assert.Throws<ArgumentException>(() => log.Append(EventTypes.Sample, Snap, "text"));
            Assert.Throws<ArgumentException>(() => log.Append(EventTypes.Sample, Snap, new[] { 1, 2 }));
            Assert.AreEqual(0, log.Events.Count, "failed appends do not use a seq");
        }

        [Test]
        public void Lines_RoundTrip()
        {
            var log = new EventLog("r");
            log.Append(EventTypes.ZoneEnter, Snap, new { from = "z_lab_302", to = "z_corr_m" });
            var back = LogEvent.ParseLine(log.Events[0].ToLine());
            Assert.AreEqual(1, back.Seq);
            Assert.AreEqual("z_corr_m", (string)back.Data["to"]);
            Assert.AreEqual(Snap.Pos, back.Pos);
        }
    }
}
