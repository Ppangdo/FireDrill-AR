using System.Collections.Generic;
using FireDrill.Core;
using FireDrill.Runtime;
using NUnit.Framework;

namespace FireDrill.Tests
{
    /// <summary>16.10 inventory rules and the 10장/16.4 smoke safety cap.</summary>
    public class InventoryAndEffectsTests
    {
        private static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>
        {
            ["o_towel"] = "towel", ["o_bag"] = "bag", ["o_phone"] = "phone", ["o_doc"] = "document", ["o_ext"] = "extinguisher",
        };

        private static Inventory Inv() => new Inventory(2, id => Kinds.TryGetValue(id, out var k) ? k : null);

        [Test]
        public void HandThenBag_ThenDropWhenFull()
        {
            var inv = Inv();
            Assert.IsNull(inv.Pickup("o_towel"));
            Assert.AreEqual("o_towel", inv.Hand);
            Assert.IsNull(inv.Pickup("o_bag"));
            CollectionAssert.AreEqual(new[] { "o_towel" }, inv.Bag);
            Assert.IsNull(inv.Pickup("o_phone"));
            CollectionAssert.AreEqual(new[] { "o_towel", "o_bag" }, inv.Bag);
            Assert.AreEqual("o_phone", inv.Pickup("o_doc"), "bag full: the held item falls");
            Assert.AreEqual("o_doc", inv.Hand);
            Assert.IsTrue(inv.HasTowel);
        }

        [Test]
        public void Towel_WetEquipAndDrop()
        {
            var inv = Inv();
            Assert.IsNull(inv.ToggleTowel(), "no towel yet");
            Assert.IsFalse(inv.WetTowel());
            inv.Pickup("o_towel");
            Assert.AreEqual(TowelState.None, inv.Towel);
            Assert.AreEqual(true, inv.ToggleTowel());
            Assert.AreEqual(TowelState.Dry, inv.Towel);
            Assert.IsTrue(inv.WetTowel());
            Assert.AreEqual(TowelState.Wet, inv.Towel);
            Assert.AreEqual("o_towel", inv.Drop());
            Assert.AreEqual(TowelState.None, inv.Towel, "dropping takes it off the face");
            Assert.IsFalse(inv.HasTowel);
            inv.Pickup("o_towel");
            Assert.IsTrue(inv.TowelWet, "still wet");
        }

        [Test]
        public void SmokeOpacity_FollowsFormula_AndNeverExceedsCap()
        {
            var rt = CoreTestData.Config<RuntimeConfig>("runtime.json");
            var fire = CoreTestData.Fire();
            Assert.AreEqual(0.7f, fire.Render.MaxOpacity, 1e-6f, "명세서 10장 safety cap");
            Assert.AreEqual(0f, SmokeRenderer.Opacity(0f, rt.Effects.Smoke, fire.Render.MaxOpacity));
            Assert.AreEqual(0.15f + 0.55f * 0.5f, SmokeRenderer.Opacity(0.5f, rt.Effects.Smoke, fire.Render.MaxOpacity), 1e-5f);
            for (float d = 0; d <= 1.0001f; d += 0.01f)
                Assert.LessOrEqual(SmokeRenderer.Opacity(d, rt.Effects.Smoke, fire.Render.MaxOpacity), fire.Render.MaxOpacity + 1e-6f);
            Assert.AreEqual(0.02f + 0.25f * 0.4f, SmokeRenderer.FogDensity(0.4f, rt.Effects.Fog), 1e-5f);
        }

        [Test]
        public void RuntimeConfig_HasStage4Sections()
        {
            var rt = CoreTestData.Config<RuntimeConfig>("runtime.json");
            Assert.AreEqual(4, rt.Effects.Body.Stages.Count);
            Assert.IsTrue(rt.Scene.Objects["towel"].Essential && rt.Scene.Objects["towel"].Pickable);
            Assert.IsFalse(rt.Scene.Objects["bag"].Essential);
            Assert.IsFalse(rt.Scene.Objects["alarm"].Pickable);
            foreach (var key in new[] { "sound", "smell", "smoke", "alarm", "stage_1", "stage_2", "stage_3", "hot_handle", "cool_handle" })
                Assert.IsTrue(rt.Effects.Subtitles.ContainsKey(key), key);
        }
    }
}
