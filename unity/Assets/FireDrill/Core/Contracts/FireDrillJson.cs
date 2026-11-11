using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace FireDrill.Core
{
    /// <summary>One JSON setup for every contract file and API body (명세서 14.1): snake_case fields and enums.</summary>
    public static class FireDrillJson
    {
        public const int SchemaVersion = 1;

        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
            Converters = { new StringEnumConverter(new SnakeCaseNamingStrategy()), new VecConverter() },
            NullValueHandling = NullValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Error,
            FloatParseHandling = FloatParseHandling.Double,
        };

        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        public static string Serialize(object value, bool indented = false) =>
            JsonConvert.SerializeObject(value, indented ? Formatting.Indented : Formatting.None, Settings);
    }
}
