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
        public ItemSettings Items { get; set; }
        public EffectSettings Effects { get; set; }
        public AudioSettings Audio { get; set; }
        public UiSettings Ui { get; set; }
        public NpcSettings Npc { get; set; }

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
            public float TalkRangeM { get; set; }
            public float PickupS { get; set; }
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
            public NpcShape Npc { get; set; }
        }

        public sealed class NpcShape
        {
            public float HeightM { get; set; }
            public float RadiusM { get; set; }
            public string Color { get; set; }
        }

        public sealed class ObjectShape
        {
            public string Shape { get; set; }
            public float[] SizeM { get; set; }
            public string Color { get; set; }
            public bool Pickable { get; set; }
            public bool Essential { get; set; }
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
            public float ReportTimeoutS { get; set; }
            public float NpcTimeoutS { get; set; }
        }

        public sealed class ItemSettings
        {
            public int BagSlots { get; set; }
            public float ExtinguisherCapacityS { get; set; }
        }

        /// <summary>16.4 smoke, 16.5 fire, 10장 body effects. Opacity = base + per_d × D, capped by fire.render.max_opacity.</summary>
        public sealed class EffectSettings
        {
            public float SmokeCueD { get; set; }
            public float SubtitleS { get; set; }
            /// <summary>Subtitle text by key: sound, smell, smoke, alarm, stage_1..3, hot_handle, cool_handle.</summary>
            public Dictionary<string, string> Subtitles { get; set; }
            public SmokeSettings Smoke { get; set; }
            public FogSettings Fog { get; set; }
            public FloorLineSettings FloorLine { get; set; }
            public FireLook Fire { get; set; }
            public BodySettings Body { get; set; }
        }

        public sealed class SmokeSettings
        {
            public float BaseOpacity { get; set; }
            public float PerDOpacity { get; set; }
            public float EdgeFadeM { get; set; }
            public float LerpS { get; set; }
            public string Color { get; set; }
        }

        public sealed class FogSettings
        {
            public float BaseDensity { get; set; }
            public float PerCDensity { get; set; }
            public string Color { get; set; }
        }

        public sealed class FloorLineSettings
        {
            public float WidthM { get; set; }
            public float HeightM { get; set; }
            public string Color { get; set; }
        }

        public sealed class FireSize
        {
            public float DiameterM { get; set; }
            public float HeightM { get; set; }
        }

        public sealed class FireLook
        {
            public FireSize Initial { get; set; }
            public FireSize Growing { get; set; }
            public float ExtinguishShrinkS { get; set; }
            public float LightIntensity { get; set; }
            public float LightRangeM { get; set; }
            public float Flicker { get; set; }
            public string FlameColor { get; set; }
            public string SmokeColor { get; set; }
            public string WhiteSmokeColor { get; set; }
        }

        public sealed class BodyStageLook
        {
            public float Vignette { get; set; }
            public float Saturation { get; set; }
            public float BlurRadius { get; set; }
        }

        public sealed class BodySettings
        {
            public List<BodyStageLook> Stages { get; set; }
            public float ShakeDeg { get; set; }
            public float ShakeHz { get; set; }
            public float HotFlashS { get; set; }
            public float BlurStartM { get; set; }
            public float BlurEndM { get; set; }
            public string HotColor { get; set; }
        }

        /// <summary>16.6 fallback sounds made in code until the .wav files arrive.</summary>
        public sealed class AudioSettings
        {
            public float Volume { get; set; }
            public float AlarmHz { get; set; }
            public float AlarmOnS { get; set; }
            public float AlarmOffS { get; set; }
            public float CrackleVolume { get; set; }
            public float HissS { get; set; }
            public float SprayVolume { get; set; }
        }

        /// <summary>16.11 UI Toolkit screens: font (file, else the OS fallback) and labels.</summary>
        public sealed class UiSettings
        {
            public string FontFile { get; set; }
            public string FallbackOsFont { get; set; }
            public int FontSamplingPt { get; set; }
            public int FontAtlasPx { get; set; }
            public int[] ReferenceResolution { get; set; }
            public Dictionary<string, string> Grades { get; set; }
            public float TimelineMaxC { get; set; }
            public Dictionary<string, string> NpcResults { get; set; }
        }

        /// <summary>16.9 NPC behaviour.</summary>
        public sealed class NpcSettings
        {
            public float WalkSpeedMps { get; set; }
            public float HeadM { get; set; }
            public float CrouchHeadM { get; set; }
            public float FollowDistanceM { get; set; }
            public float LoseDistanceM { get; set; }
            public float DoorPauseS { get; set; }
            public float SmokePathCost { get; set; }
            public float IgnoreRadiusM { get; set; }
            public float IgnoreS { get; set; }
            public float NameTagHeightM { get; set; }
            public float NameTagSize { get; set; }
            public float ArriveM { get; set; }
            public float ReplanS { get; set; }
        }
    }
}
