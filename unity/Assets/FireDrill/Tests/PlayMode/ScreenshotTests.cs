using System.Collections;
using System.IO;
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
    }
}
