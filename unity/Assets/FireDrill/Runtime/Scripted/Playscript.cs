using System.Collections.Generic;
using FireDrill.Core;

namespace FireDrill.Runtime
{
    /// <summary>contracts/playscripts/*.json (playscript.schema.json, 명세서 15.2).</summary>
    public sealed class Playscript
    {
        public int SchemaVersion { get; set; }
        public string Name { get; set; }
        public float TimeScale { get; set; }
        public string Start { get; set; }
        public List<Step> Steps { get; set; }
        public ExpectBlock Expect { get; set; }

        public sealed class Step
        {
            public string Do { get; set; }
            public float[] Pos { get; set; }
            public string Exit { get; set; }
            public float Seconds { get; set; }
            public string Event { get; set; }
            public bool On { get; set; }
            public string Kind { get; set; }
            public string Target { get; set; }
            public float HoldS { get; set; }
            public string Intent { get; set; }
            /// <summary>toggle_door only: skip when the door is already in this state.</summary>
            public bool? WantOpen { get; set; }

            public override string ToString() => Do + (Target != null ? " " + Target : "") + (Kind != null ? " " + Kind : "")
                                                 + (Pos != null ? " [" + string.Join(", ", Pos) + "]" : "") + (Exit != null ? " " + Exit : "");
        }

        public sealed class ExpectBlock
        {
            public string Outcome { get; set; }
            public Dictionary<string, string> Grades { get; set; }
            public Dictionary<string, string> NpcResults { get; set; }
        }

        public static Playscript Parse(string json) => FireDrillJson.Deserialize<Playscript>(json);
    }
}
