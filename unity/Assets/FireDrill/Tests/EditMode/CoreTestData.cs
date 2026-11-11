using System.IO;
using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Tests
{
    /// <summary>Demo config and golden files for the Stage 1 Core tests.</summary>
    internal static class CoreTestData
    {
        public static string ConfigDir => Path.Combine(Application.dataPath, "FireDrill", "Config");
        public static string RepoDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        public static string GoldenDir => Path.Combine(RepoDir, "contracts", "golden");

        public static string ConfigText(string file) => File.ReadAllText(Path.Combine(ConfigDir, file));
        public static T Config<T>(string file) => FireDrillJson.Deserialize<T>(ConfigText(file));

        public static ZoneGraph Graph() => ZoneGraph.Load(ConfigText("zone_graph.json"));
        public static FireConfig Fire() => Config<FireConfig>("fire.json");
        public static Scenario Scenario() => Config<Scenario>("scenario.json");
        public static EvaluationConfig Evaluation() => Config<EvaluationConfig>("evaluation.json");

        public static LogEvent[] GoldenLog(string name) =>
            File.ReadAllLines(Path.Combine(GoldenDir, name + ".jsonl")).Where(l => l.Trim().Length > 0).Select(LogEvent.ParseLine).ToArray();
    }
}
