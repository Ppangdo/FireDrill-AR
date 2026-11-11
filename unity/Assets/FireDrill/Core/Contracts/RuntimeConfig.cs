using System.Collections.Generic;

namespace FireDrill.Core
{
    /// <summary>runtime.json: PC controls, scene building, flow timing and networking numbers
    /// (명세서 10·14.6·16.2·16.7). Standing head height comes from evaluation.json standing_head_m.</summary>
    public sealed class RuntimeConfig
    {
        public int SchemaVersion { get; set; }
        public PcControls Pc { get; set; }
        public SceneSettings Scene { get; set; }
        public FlowSettings Flow { get; set; }
        public NetworkSettings Network { get; set; }

        public sealed class PcControls
        {
            public float WalkSpeedMps { get; set; }
            public float CrouchSpeedMps { get; set; }
            public float CrouchHeadM { get; set; }
            public float PostureSwitchS { get; set; }
            public float BodyRadiusM { get; set; }
            public float MouseSensitivityDeg { get; set; }
            public float PitchLimitDeg { get; set; }
            public float InteractRangeM { get; set; }
            public float HandleRangeM { get; set; }
            public float HandleHoldS { get; set; }
            public float FaucetHoldS { get; set; }
            public float AbortHoldS { get; set; }
        }

        public sealed class SceneSettings
        {
            public float WallThicknessM { get; set; }
            public float DoorThicknessM { get; set; }
            public float DoorTopM { get; set; }
            public float DoorSwingS { get; set; }
            public float[] HandleSizeM { get; set; }
            public float[] ExitSignSizeM { get; set; }
            public float ExitSignHeightM { get; set; }
            public float LightIntensity { get; set; }
            public float LightRangeM { get; set; }
            public Dictionary<string, string> Colors { get; set; }
            /// <summary>Placeholder shape per object kind (snake_case kind → shape), 16.10.</summary>
            public Dictionary<string, ObjectShape> Objects { get; set; }
        }

        public sealed class ObjectShape
        {
            public string Shape { get; set; }
            public float[] SizeM { get; set; }
            public string Color { get; set; }
        }

        public sealed class FlowSettings
        {
            public float TickS { get; set; }
            public float SampleIntervalS { get; set; }
            public float EndingS { get; set; }
            public float RunCreateWaitS { get; set; }
        }

        public sealed class NetworkSettings
        {
            public float RequestTimeoutS { get; set; }
            public float UploadIntervalS { get; set; }
            public int UploadBatchMax { get; set; }
            public List<float> RetryDelaysS { get; set; }
            public float OfflineProbeS { get; set; }
            public float WsStatusIntervalS { get; set; }
            public float WsPingTimeoutS { get; set; }
            public float WsReconnectS { get; set; }
        }
    }
}
