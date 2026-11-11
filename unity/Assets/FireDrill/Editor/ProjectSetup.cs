using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace FireDrill.Editor
{
    /// <summary>Project setup that people must not do by hand (명세서 16.1, 16.2). Run in batch mode:
    /// Unity -batchmode -quit -projectPath unity -executeMethod FireDrill.Editor.ProjectSetup.Run</summary>
    public static class ProjectSetup
    {
        public const string SettingsDir = "Assets/FireDrill/Settings";
        public const string ScenePath = "Assets/FireDrill/Scenes/Bootstrap.unity";

        public static void Run()
        {
            SetupRendering();
            SetupPlayer();
            SceneSetup.CreateBootstrap();
            AssetDatabase.SaveAssets();
            Debug.Log("[FireDrill] project setup done");
        }

        /// <summary>URP asset + renderer, used by every quality level; URP shaders found by name stay in builds.</summary>
        public static void SetupRendering()
        {
            Directory.CreateDirectory(SettingsDir);
            var rendererPath = SettingsDir + "/FireDrill_Renderer.asset";
            var assetPath = SettingsDir + "/FireDrill_URP.asset";

            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }
            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assetPath);
            if (urp == null)
            {
                urp = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(urp, assetPath);
            }
            GraphicsSettings.defaultRenderPipeline = urp;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = urp;
                QualitySettings.vSyncCount = 1;
            }
            QualitySettings.SetQualityLevel(current, false);

            // SceneBuilder makes materials with Shader.Find: keep those shaders in player builds.
            var graphics = new SerializedObject(AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset"));
            var list = graphics.FindProperty("m_AlwaysIncludedShaders");
            foreach (var name in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit" })
            {
                var shader = Shader.Find(name);
                if (shader == null) { Debug.LogWarning("[FireDrill] shader not found: " + name); continue; }
                bool present = Enumerable.Range(0, list.arraySize).Any(i => list.GetArrayElementAtIndex(i).objectReferenceValue == shader);
                if (present) continue;
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            }
            graphics.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>1920×1080 windowed, 60 fps target (명세서 16.1).</summary>
        public static void SetupPlayer()
        {
            PlayerSettings.productName = "FireDrill";
            PlayerSettings.companyName = "Woorineunhandas";
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1920;
            PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
        }
    }

    public static class SceneSetup
    {
        /// <summary>Bootstrap.unity holds exactly one object, Bootstrap (명세서 16.2). Everything else is built at run time.</summary>
        public static void CreateBootstrap()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectSetup.ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Bootstrap");
            go.AddComponent<FireDrill.Runtime.Bootstrap>();
            EditorSceneManager.SaveScene(scene, ProjectSetup.ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ProjectSetup.ScenePath, true) };
            Debug.Log("[FireDrill] wrote " + ProjectSetup.ScenePath);
        }
    }

    public static class Build
    {
        /// <summary>Unity -batchmode -quit -projectPath unity -executeMethod FireDrill.Editor.Build.Pc
        /// → unity/Builds/Pc/FireDrill.exe with Config copied to StreamingAssets.</summary>
        public static void Pc()
        {
            ProjectSetup.Run();
            var streaming = "Assets/StreamingAssets/FireDrill/Config";
            Directory.CreateDirectory(streaming);
            foreach (var f in Directory.GetFiles("Assets/FireDrill/Config", "*.json"))
                File.Copy(f, Path.Combine(streaming, Path.GetFileName(f)), true);
            AssetDatabase.Refresh();
            try
            {
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ProjectSetup.ScenePath },
                    locationPathName = "Builds/Pc/FireDrill.exe",
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,
                });
                Debug.Log("[FireDrill] build " + report.summary.result + " → " + report.summary.outputPath);
                if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) EditorApplication.Exit(1);
            }
            finally
            {
                AssetDatabase.DeleteAsset("Assets/StreamingAssets");
            }
        }
    }
}
