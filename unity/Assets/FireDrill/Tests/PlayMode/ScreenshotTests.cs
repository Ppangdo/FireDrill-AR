using System.Collections;
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
    /// <summary>명세서 15.2: screenshots from five fixed spots for a person to judge (out/screenshots/stage3).
    /// Needs a graphics device: run PlayMode tests without -nographics. Skipped otherwise.</summary>
    public class ScreenshotTests
    {
        // name, eye position (building coordinates), yaw, pitch. Test data, not tuning.
        private static readonly (string name, Vector3 pos, float yaw, float pitch)[] Spots =
        {
            ("1_lab302_spawn", new Vector3(6f, 1.65f, 13f), 270f, 5f),
            ("2_corridor_mid_south", new Vector3(1f, 1.65f, 15.5f), 180f, 5f),
            ("3_corridor_south_exit", new Vector3(1f, 1.65f, 5f), 180f, 5f),
            ("4_elevator_lobby", new Vector3(1.5f, 1.65f, 12f), 270f, 5f),
            ("5_overview", new Vector3(3f, 22f, 12f), 0f, 90f),
        };

        [UnityTest]
        public IEnumerator FiveFixedSpots()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("no graphics device (-nographics): screenshots skipped");

            var cfg = ConfigSet.Load();
            var root = new GameObject("Root");
            var view = SceneBuilder.Build(cfg.Graph, cfg.Runtime.Scene, root.transform);
            view.Doors["e_302_door"].SetOpen(true, instant: true);
            view.Doors["e_stair_b"].SetOpen(true, instant: true);
            var camGo = new GameObject("ShotCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.fieldOfView = 70f;
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            yield return null;

            var dir = Path.Combine(Paths.Repo, "out", "screenshots", "stage3");
            Directory.CreateDirectory(dir);
            foreach (var (name, pos, yaw, pitch) in Spots)
            {
                camGo.transform.SetPositionAndRotation(pos, Quaternion.Euler(pitch, yaw, 0));
                yield return null;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
                Object.Destroy(tex);
            }
            cam.targetTexture = null;
            rt.Release();
            Object.Destroy(camGo);
            Object.Destroy(root);
            Assert.AreEqual(Spots.Length, Directory.GetFiles(dir, "*.png").Length);
        }

        // Stage 4: the fire 80 s after ignition with the storage door burst open (scenario door_events).
        private static readonly (string name, Vector3 pos, float yaw, float pitch)[] SmokeSpots =
        {
            ("1_corridor_mid_north_standing", new Vector3(1f, 1.65f, 12f), 0f, 0f),
            ("2_corridor_mid_north_crouched", new Vector3(1f, 0.9f, 12f), 0f, 0f),
            ("3_corridor_north_fire_room", new Vector3(1f, 1.2f, 20f), 90f, 5f),
            ("4_corridor_south_exit", new Vector3(1f, 1.65f, 6f), 180f, 0f),
            ("5_overview", new Vector3(3f, 22f, 12f), 0f, 90f),
        };

        private sealed class CameraRig : IPlayerRig
        {
            public Transform T;
            public Vec3 Head => new Vec3(T.position.x, T.position.y, T.position.z);
            public Vec3 Forward => new Vec3(T.forward.x, T.forward.y, T.forward.z);
            public float StandingHeight => 1.65f;
            public void Teleport(Vec3 feet, float yawDeg) { }
        }

        [UnityTest]
        public IEnumerator Stage4_SmokeAndFire()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("no graphics device (-nographics): screenshots skipped");

            var cfg = ConfigSet.Load();
            var root = new GameObject("Root");
            var view = SceneBuilder.Build(cfg.Graph, cfg.Runtime.Scene, root.transform);
            var engine = new FireEngine();
            engine.Reset(cfg.Graph, cfg.Fire, cfg.Scenario);
            engine.Ignite(0f);
            float tick = cfg.Fire.TickS;
            for (float t = tick; t <= 80f + 1e-3f; t += tick)
            {
                foreach (var e in cfg.Scenario.DoorEvents)
                    if (Mathf.Abs(t - e.AtS) < tick / 2f) { engine.SetDoor(e.Edge, true); view.Doors[e.Edge].SetOpen(true, instant: true); }
                engine.Tick(t, tick);
            }
            view.Doors["e_stair_b"].SetOpen(true, instant: true);

            var camGo = new GameObject("ShotCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.fieldOfView = 70f;
            var rig = new CameraRig { T = camGo.transform };
            var smoke = new GameObject("Smoke").AddComponent<SmokeRenderer>();
            smoke.Init(cfg.Graph, engine, rig, cfg.Fire, cfg.Runtime);
            smoke.Snap();
            var fire = new GameObject("Fire").AddComponent<FireVfx>();
            fire.Init(cfg.Graph, cfg.Scenario.Fire.OriginZone, engine, cfg.Runtime);
            yield return new WaitForSeconds(1.5f); // let the particles fill

            var rtex = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rtex;
            var dir = Path.Combine(Paths.Repo, "out", "screenshots", "stage4");
            Directory.CreateDirectory(dir);
            foreach (var (name, pos, yaw, pitch) in SmokeSpots)
            {
                camGo.transform.SetPositionAndRotation(pos, Quaternion.Euler(pitch, yaw, 0));
                yield return null; // fog follows the camera head
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rtex;
                var tex = new Texture2D(rtex.width, rtex.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rtex.width, rtex.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
                Object.Destroy(tex);
            }
            Assert.LessOrEqual(smoke.SmokeMaterials.Max(m => m.color.a), cfg.Fire.Render.MaxOpacity + 1e-5f, "opacity cap");
            cam.targetTexture = null;
            rtex.Release();
            RenderSettings.fog = false;
            Object.Destroy(camGo);
            Object.Destroy(smoke.gameObject);
            Object.Destroy(fire.gameObject);
            Object.Destroy(root);
            Assert.AreEqual(SmokeSpots.Length, Directory.GetFiles(dir, "*.png").Length);
        }
    }
}
