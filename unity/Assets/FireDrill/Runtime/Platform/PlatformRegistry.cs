using System;
using System.Collections.Generic;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>A platform adapter set (명세서 14.3): the trainee body, the input source and the space mapping.</summary>
    public interface IPlatform
    {
        IPlayerRig Rig { get; }
        IInteractionSource Input { get; }
        ISpaceProvider Space { get; }
        /// <summary>Movement and input on/off (paused, aborted, outside a run's playable states).</summary>
        bool InputEnabled { get; set; }
        /// <summary>Camera the overlay and screenshots use, may be null.</summary>
        Camera Camera { get; }
        /// <summary>Body stage 3 view shake (명세서 16.11, PC only). 0 = off.</summary>
        void SetShake(float amplitudeDeg, float hz);
        /// <summary>Dialogue choice key pressed (0-based), for the open conversation.</summary>
        event System.Action<int> ChoiceKey;
    }

    public sealed class PlatformContext
    {
        public ConfigSet Config;
        public Transform Parent;
        public BuildingView Building;
        /// <summary>Extra arguments (e.g. play script path for "scripted").</summary>
        public IReadOnlyDictionary<string, string> Args = new Dictionary<string, string>();
    }

    /// <summary>Platform assemblies register a factory by name (app_config.platform: "pc", later "quest";
    /// tests use "scripted"). Bootstrap stays independent of every platform assembly.</summary>
    public static class PlatformRegistry
    {
        private static readonly Dictionary<string, Func<PlatformContext, IPlatform>> Factories =
            new Dictionary<string, Func<PlatformContext, IPlatform>>();

        public static void Register(string name, Func<PlatformContext, IPlatform> factory) => Factories[name] = factory;

        public static bool Has(string name) => Factories.ContainsKey(name);

        public static IPlatform Create(string name, PlatformContext ctx)
        {
            if (!Factories.TryGetValue(name, out var f))
                throw new InvalidOperationException($"no platform '{name}' registered (have: {string.Join(", ", Factories.Keys)})");
            return f(ctx);
        }

        /// <summary>Identity mapping: PC building coordinates are world coordinates.</summary>
        public sealed class IdentitySpace : ISpaceProvider
        {
            public bool Ready => true;
            public Matrix4x4 BuildingToWorld => Matrix4x4.identity;
        }
    }
}
