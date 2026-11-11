using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FireDrill.Core
{
    /// <summary>
    /// In-memory behaviour log of one run (명세서 6장). seq starts at 1 and increases by one per Append.
    /// Subscribers to Appended (file writer, server uploader) see every event in order.
    /// </summary>
    public sealed class EventLog : IEventLog
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(FireDrillJson.Settings);
        private readonly List<LogEvent> _events = new List<LogEvent>();

        public EventLog(string runId)
        {
            if (string.IsNullOrEmpty(runId)) throw new ArgumentException("runId is required", nameof(runId));
            RunId = runId;
        }

        public string RunId { get; }
        public IReadOnlyList<LogEvent> Events => _events;
        public event Action<LogEvent> Appended;

        /// <summary>
        /// data: null, a JObject (copied), a dictionary or an anonymous object. Property names of
        /// objects are written in snake_case; dictionary keys are kept as given.
        /// </summary>
        public LogEvent Append(string type, Snapshot s, object data = null)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentException("type is required", nameof(type));
            var e = new LogEvent(RunId, _events.Count + 1, type, s, ToJObject(data));
            _events.Add(e);
            Appended?.Invoke(e);
            return e;
        }

        internal static JObject ToJObject(object data)
        {
            switch (data)
            {
                case null: return new JObject();
                case JObject j: return (JObject)j.DeepClone();
                case string _:
                case JToken _:
                    throw new ArgumentException("data must be an object (anonymous object, dictionary or JObject)", nameof(data));
                default:
                    var token = JToken.FromObject(data, Serializer);
                    return token as JObject
                           ?? throw new ArgumentException($"data must serialize to a JSON object, got {token.Type}", nameof(data));
            }
        }
    }
}
