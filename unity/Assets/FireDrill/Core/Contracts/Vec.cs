using System;
using Newtonsoft.Json;

namespace FireDrill.Core
{
    /// <summary>Building coordinates in metres, Y up (명세서 14.1). Serialized as [x, y, z].</summary>
    [JsonConverter(typeof(VecConverter))]
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, float k) => new Vec3(a.X * k, a.Y * k, a.Z * k);
        public static float Distance(Vec3 a, Vec3 b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        public Vec2 Xz => new Vec2(X, Z);

        public bool Equals(Vec3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"[{X}, {Y}, {Z}]";
    }

    /// <summary>Floor-plane coordinates [x, z] (명세서 14.1).</summary>
    [JsonConverter(typeof(VecConverter))]
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public readonly float X, Z;
        public Vec2(float x, float z) { X = x; Z = z; }

        public static float Distance(Vec2 a, Vec2 b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        public bool Equals(Vec2 o) => X == o.X && Z == o.Z;
        public override bool Equals(object obj) => obj is Vec2 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Z);
        public override string ToString() => $"[{X}, {Z}]";
    }

    /// <summary>Vec2/Vec3 and their nullable forms as JSON arrays. Registered globally in FireDrillJson.Settings.</summary>
    public sealed class VecConverter : JsonConverter
    {
        public override bool CanConvert(Type t) =>
            t == typeof(Vec3) || t == typeof(Vec3?) || t == typeof(Vec2) || t == typeof(Vec2?);

        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            switch (value)
            {
                case null: w.WriteNull(); break;
                case Vec3 v: w.WriteStartArray(); w.WriteValue(v.X); w.WriteValue(v.Y); w.WriteValue(v.Z); w.WriteEndArray(); break;
                case Vec2 v: w.WriteStartArray(); w.WriteValue(v.X); w.WriteValue(v.Z); w.WriteEndArray(); break;
                default: throw new JsonSerializationException($"VecConverter cannot write {value.GetType()}");
            }
        }

        public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer s)
        {
            if (r.TokenType == JsonToken.Null)
            {
                if (t == typeof(Vec3?) || t == typeof(Vec2?)) return null;
                throw new JsonSerializationException($"Null is not allowed at {r.Path}");
            }
            string path = r.Path;
            var a = s.Deserialize<float[]>(r);
            bool is3 = t == typeof(Vec3) || t == typeof(Vec3?);
            int n = is3 ? 3 : 2;
            if (a == null || a.Length != n) throw new JsonSerializationException($"Expected {(is3 ? "[x, y, z]" : "[x, z]")} at {path}");
            return is3 ? (object)new Vec3(a[0], a[1], a[2]) : new Vec2(a[0], a[1]);
        }
    }
}
