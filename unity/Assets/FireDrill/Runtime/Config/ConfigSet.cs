using System;
using System.IO;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>All config files (명세서 14.5), loaded once by Bootstrap.
    /// Editor and play-mode tests read Assets/FireDrill/Config; a player build reads StreamingAssets/FireDrill/Config.</summary>
    public sealed class ConfigSet
    {
        public string Directory { get; private set; }
        public AppConfig App { get; private set; }
        public ZoneGraph Graph { get; private set; }
        public Scenario Scenario { get; private set; }
        public FireConfig Fire { get; private set; }
        public EvaluationConfig Evaluation { get; private set; }
        public DialogueData Dialogue { get; private set; }
        public NpcCache NpcCache { get; private set; }
        public RuntimeConfig Runtime { get; private set; }

        public static string DefaultDirectory =>
            Application.isEditor
                ? Path.Combine(Application.dataPath, "FireDrill", "Config")
                : Path.Combine(Application.streamingAssetsPath, "FireDrill", "Config");

        /// <summary>Throws ConfigException naming the file and field path when anything is wrong.</summary>
        public static ConfigSet Load(string directory = null)
        {
            var dir = directory ?? DefaultDirectory;
            var set = new ConfigSet { Directory = dir };
            set.App = Read<AppConfig>(dir, "app_config.json");
            set.Graph = Wrap("zone_graph.json", () => ZoneGraph.Load(Text(dir, "zone_graph.json")));
            set.Scenario = Read<Scenario>(dir, "scenario.json");
            set.Fire = Read<FireConfig>(dir, "fire.json");
            set.Evaluation = Read<EvaluationConfig>(dir, "evaluation.json");
            set.Dialogue = Read<DialogueData>(dir, "dialogue.json");
            set.NpcCache = Read<NpcCache>(dir, "npc_cache.json");
            set.Runtime = Read<RuntimeConfig>(dir, "runtime.json");

            if (set.Scenario.BuildingId != set.Graph.Data.BuildingId)
                throw new ConfigException("scenario.json", $"$.building_id: {set.Scenario.BuildingId} != zone_graph {set.Graph.Data.BuildingId}");
            if (set.App.ScenarioId != set.Scenario.Id)
                throw new ConfigException("app_config.json", $"$.scenario_id: {set.App.ScenarioId} != scenario {set.Scenario.Id}");
            return set;
        }

        private static string Text(string dir, string file)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) throw new ConfigException(file, $"file not found: {path}");
            return File.ReadAllText(path);
        }

        private static T Read<T>(string dir, string file) => Wrap(file, () => FireDrillJson.Deserialize<T>(Text(dir, file)));

        private static T Wrap<T>(string file, Func<T> load)
        {
            try { return load(); }
            catch (ConfigException) { throw; }
            catch (Exception e) { throw new ConfigException(file, e.Message); }
        }
    }

    public sealed class ConfigException : Exception
    {
        public string File { get; }
        public ConfigException(string file, string message) : base($"{file}: {message}") { File = file; }
    }
}
