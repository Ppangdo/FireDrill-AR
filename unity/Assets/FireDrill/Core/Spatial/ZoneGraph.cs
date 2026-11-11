using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FireDrill.Core
{
    /// <summary>
    /// Spatial model shared by fire engine, evaluator and NPCs (명세서 3장, 14.2, 16.8).
    /// Reference implementation of the distance rules: contracts/tools/ref_eval.py ZoneGraph.
    /// </summary>
    public sealed class ZoneGraph
    {
        public ZoneGraphData Data { get; }

        private readonly Dictionary<string, Zone> _zones;
        private readonly Dictionary<string, Edge> _edges;
        private readonly Dictionary<string, List<Edge>> _edgesOf;
        private readonly Dictionary<string, double> _area;
        private readonly HashSet<string> _blocked = new HashSet<string>();
        /// <summary>Per-edge distance to the nearest exit along edge centres (16.8). Recomputed when blocking changes.</summary>
        private Dictionary<string, double> _exitDist;

        private ZoneGraph(ZoneGraphData data)
        {
            Data = data;
            _zones = data.Zones.ToDictionary(z => z.Id);
            _edges = data.Edges.ToDictionary(e => e.Id);
            _edgesOf = data.Zones.ToDictionary(z => z.Id, _ => new List<Edge>());
            foreach (var e in data.Edges)
            {
                _edgesOf[e.From].Add(e);
                _edgesOf[e.To].Add(e);
            }
            _area = data.Zones.ToDictionary(z => z.Id, z => PolygonArea(z.Polygon));
            _exitDist = ComputeExitDistances();
        }

        // ── loading ────────────────────────────────────────────────────────────

        /// <summary>Parse and validate. Throws ZoneGraphException naming the bad field path.</summary>
        public static ZoneGraph Load(string json)
        {
            JObject root;
            try
            {
                var token = JToken.Parse(json);
                root = token as JObject ?? throw new ZoneGraphException("$: expected an object");
            }
            catch (JsonReaderException ex)
            {
                throw new ZoneGraphException($"{(string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path)}: invalid JSON ({ex.Message})");
            }

            Validator.Root(root);

            ZoneGraphData data;
            try
            {
                data = root.ToObject<ZoneGraphData>(JsonSerializer.Create(FireDrillJson.Settings));
            }
            catch (JsonException ex)
            {
                throw new ZoneGraphException($"{PathOf(ex)}: {ex.Message}");
            }

            Validator.References(data);
            return new ZoneGraph(data);
        }

        private static string PathOf(JsonException ex) =>
            ex is JsonSerializationException s && !string.IsNullOrEmpty(s.Path) ? s.Path
            : ex is JsonReaderException r && !string.IsNullOrEmpty(r.Path) ? r.Path
            : "$";

        /// <summary>Hand-written mirror of contracts/schemas/zone_graph.schema.json, so errors carry field paths.</summary>
        private static class Validator
        {
            static readonly Regex BuildingId = new Regex("^[a-z0-9_]+$");
            static readonly Regex ZoneId = new Regex("^z_[a-z0-9_]+$");
            static readonly Regex EdgeId = new Regex("^e_[a-z0-9_]+$");
            static readonly Regex ObjectId = new Regex("^o_[a-z0-9_]+$");
            static readonly string[] ZoneTypes = { "room", "corridor", "stair", "elevator_lobby", "exit", "refuge" };
            static readonly string[] EdgeTypes = { "door", "opening", "stair" };
            static readonly string[] ObjectKinds = { "towel", "extinguisher", "phone", "document", "bag", "alarm", "faucet", "elevator_button", "window" };

            static ZoneGraphException Fail(string path, string msg) => new ZoneGraphException($"{path}: {msg}");

            static void Only(JObject o, string path, params string[] allowed)
            {
                foreach (var p in o.Properties())
                    if (!allowed.Contains(p.Name)) throw Fail($"{path}.{p.Name}", "unknown field");
            }

            static JToken Req(JObject o, string path, string name)
            {
                var t = o[name];
                if (t == null) throw Fail($"{path}.{name}", "required field is missing");
                return t;
            }

            static string Str(JObject o, string path, string name, Regex pattern = null, string[] oneOf = null)
            {
                var t = Req(o, path, name);
                if (t.Type != JTokenType.String) throw Fail($"{path}.{name}", "must be a string");
                var v = (string)t;
                if (v.Length == 0) throw Fail($"{path}.{name}", "must not be empty");
                if (pattern != null && !pattern.IsMatch(v)) throw Fail($"{path}.{name}", $"\"{v}\" does not match {pattern}");
                if (oneOf != null && !oneOf.Contains(v)) throw Fail($"{path}.{name}", $"\"{v}\" is not one of {string.Join(", ", oneOf)}");
                return v;
            }

            static double Num(JToken t, string path)
            {
                if (t.Type != JTokenType.Float && t.Type != JTokenType.Integer) throw Fail(path, "must be a number");
                return (double)t;
            }

            static void Positive(JObject o, string path, string name)
            {
                if (Num(Req(o, path, name), $"{path}.{name}") <= 0) throw Fail($"{path}.{name}", "must be > 0");
            }

            static void Vec(JToken t, string path, int n)
            {
                if (!(t is JArray a) || a.Count != n) throw Fail(path, n == 3 ? "must be [x, y, z]" : "must be [x, z]");
                for (int i = 0; i < n; i++) Num(a[i], $"{path}[{i}]");
            }

            static JArray Arr(JObject o, string path, string name, int min = 0)
            {
                var t = Req(o, path, name);
                if (!(t is JArray a)) throw Fail($"{path}.{name}", "must be an array");
                if (a.Count < min) throw Fail($"{path}.{name}", $"needs at least {min} item(s)");
                return a;
            }

            static JObject Obj(JToken t, string path) => t as JObject ?? throw Fail(path, "must be an object");

            public static void Root(JObject root)
            {
                const string p = "$";
                Only(root, p, "schema_version", "building_id", "version", "anchors", "zones", "edges");
                var sv = Req(root, p, "schema_version");
                if (sv.Type != JTokenType.Integer || (int)sv != FireDrillJson.SchemaVersion) throw Fail("$.schema_version", $"must be {FireDrillJson.SchemaVersion}");
                Str(root, p, "building_id", BuildingId);
                var ver = Req(root, p, "version");
                if (ver.Type != JTokenType.Integer || (int)ver < 1) throw Fail("$.version", "must be an integer >= 1");
                var anchors = Obj(Req(root, p, "anchors"), "$.anchors");
                Only(anchors, "$.anchors", "A", "B");
                Vec(Req(anchors, "$.anchors", "A"), "$.anchors.A", 3);
                Vec(Req(anchors, "$.anchors", "B"), "$.anchors.B", 3);

                var zones = Arr(root, p, "zones", 1);
                for (int i = 0; i < zones.Count; i++) Zone(Obj(zones[i], $"$.zones[{i}]"), $"$.zones[{i}]");
                var edges = Arr(root, p, "edges");
                for (int i = 0; i < edges.Count; i++) Edge(Obj(edges[i], $"$.edges[{i}]"), $"$.edges[{i}]");
            }

            static void Zone(JObject z, string p)
            {
                Only(z, p, "id", "type", "name", "polygon", "ceiling_h", "spawn", "objects");
                Str(z, p, "id", ZoneId);
                Str(z, p, "type", oneOf: ZoneTypes);
                Str(z, p, "name");
                var poly = Arr(z, p, "polygon", 3);
                for (int i = 0; i < poly.Count; i++) Vec(poly[i], $"{p}.polygon[{i}]", 2);
                Positive(z, p, "ceiling_h");
                if (z["spawn"] != null) Vec(z["spawn"], $"{p}.spawn", 3);
                var objs = Arr(z, p, "objects");
                for (int i = 0; i < objs.Count; i++)
                {
                    string op = $"{p}.objects[{i}]";
                    var o = Obj(objs[i], op);
                    Only(o, op, "id", "kind", "pos");
                    Str(o, op, "id", ObjectId);
                    Str(o, op, "kind", oneOf: ObjectKinds);
                    Vec(Req(o, op, "pos"), $"{op}.pos", 3);
                }
            }

            static void Edge(JObject e, string p)
            {
                Only(e, p, "id", "from", "to", "type", "width", "center", "wall_axis", "door_open", "handle", "side_from_handle");
                Str(e, p, "id", EdgeId);
                Str(e, p, "from", ZoneId);
                Str(e, p, "to", ZoneId);
                Str(e, p, "type", oneOf: EdgeTypes);
                Positive(e, p, "width");
                Vec(Req(e, p, "center"), $"{p}.center", 2);
                Str(e, p, "wall_axis", oneOf: new[] { "x", "z" });
                if (e["door_open"] != null && e["door_open"].Type != JTokenType.Boolean) throw Fail($"{p}.door_open", "must be true or false");
                if (e["handle"] != null && e["handle"].Type != JTokenType.Null) Vec(e["handle"], $"{p}.handle", 3);
                if (e["side_from_handle"] != null && e["side_from_handle"].Type != JTokenType.Null) Str(e, p, "side_from_handle", ZoneId);
            }

            public static void References(ZoneGraphData d)
            {
                var zoneIds = new HashSet<string>();
                for (int i = 0; i < d.Zones.Count; i++)
                    if (!zoneIds.Add(d.Zones[i].Id)) throw Fail($"$.zones[{i}].id", $"duplicate zone id \"{d.Zones[i].Id}\"");
                var edgeIds = new HashSet<string>();
                for (int i = 0; i < d.Edges.Count; i++)
                {
                    var e = d.Edges[i];
                    string p = $"$.edges[{i}]";
                    if (!edgeIds.Add(e.Id)) throw Fail($"{p}.id", $"duplicate edge id \"{e.Id}\"");
                    if (!zoneIds.Contains(e.From)) throw Fail($"{p}.from", $"unknown zone \"{e.From}\"");
                    if (!zoneIds.Contains(e.To)) throw Fail($"{p}.to", $"unknown zone \"{e.To}\"");
                    if (e.From == e.To) throw Fail($"{p}.to", "an edge must join two different zones");
                    if (e.SideFromHandle != null && e.SideFromHandle != e.From && e.SideFromHandle != e.To)
                        throw Fail($"{p}.side_from_handle", $"\"{e.SideFromHandle}\" is not one of the edge's zones");
                }
                if (!d.Zones.Any(z => z.Type == ZoneType.Exit)) throw Fail("$.zones", "needs at least one zone of type exit");
            }
        }

        // ── queries ────────────────────────────────────────────────────────────

        public IReadOnlyList<Zone> Zones => Data.Zones;
        public IReadOnlyList<Edge> Edges => Data.Edges;

        public Zone Zone(string zoneId) =>
            zoneId != null && _zones.TryGetValue(zoneId, out var z) ? z : throw new KeyNotFoundException($"Unknown zone \"{zoneId}\"");

        internal Edge Edge(string edgeId) =>
            edgeId != null && _edges.TryGetValue(edgeId, out var e) ? e : throw new KeyNotFoundException($"Unknown edge \"{edgeId}\"");

        /// <summary>Every edge touching the zone, blocked ones included (blocking only affects paths and distances).</summary>
        public IEnumerable<Edge> EdgesOf(string zoneId)
        {
            Zone(zoneId);
            return _edgesOf[zoneId];
        }

        public bool IsExit(string zoneId) => Zone(zoneId).Type == ZoneType.Exit;

        /// <summary>Floor area (m²) from the polygon (명세서 3장 파생 값).</summary>
        internal double Area(string zoneId) { Zone(zoneId); return _area[zoneId]; }

        internal bool IsBlocked(string edgeId) => _blocked.Contains(edgeId);

        internal static string Other(Edge e, string zoneId) => e.From == zoneId ? e.To : e.From;

        /// <summary>Zone whose floor polygon contains pos (x, z), or null. Points on a shared wall go to the first zone listed.</summary>
        public string ZoneAt(Vec3 pos)
        {
            string onBoundary = null;
            foreach (var z in Data.Zones)
            {
                var c = Contains(z.Polygon, pos.X, pos.Z);
                if (c == Containment.Inside) return z.Id;
                if (c == Containment.Boundary && onBoundary == null) onBoundary = z.Id;
            }
            return onBoundary;
        }

        public void SetBlocked(string edgeId, bool blocked)
        {
            Edge(edgeId);
            bool changed = blocked ? _blocked.Add(edgeId) : _blocked.Remove(edgeId);
            if (changed) _exitDist = ComputeExitDistances();
        }

        // ── exit distance (16.8) ───────────────────────────────────────────────

        /// <summary>
        /// Continuous distance to the nearest exit (명세서 16.8): 0 inside an exit zone; otherwise
        /// min over edges e of the current zone of |pos.xz − e.center| + D(e), where D(e) is the shortest
        /// edge-centre path from e to any edge touching an exit zone (D = 0 there). Blocked edges excluded,
        /// closed doors passable. Null if pos is outside every zone.
        /// </summary>
        public float? ExitDistance(Vec3 pos) => ExitDistance(ZoneAt(pos), pos);

        /// <summary>Same as ExitDistance(pos) with the zone given (the evaluator uses the zone recorded in the log).</summary>
        internal float? ExitDistance(string zoneId, Vec3 pos)
        {
            if (zoneId == null) return null;
            if (IsExit(zoneId)) return 0f;
            double best = double.PositiveInfinity;
            foreach (var e in _edgesOf[zoneId])
            {
                if (_blocked.Contains(e.Id)) continue;
                best = Math.Min(best, Dist(pos.X, pos.Z, e.Center) + _exitDist[e.Id]);
            }
            return (float)best;
        }

        private Dictionary<string, double> ComputeExitDistances()
        {
            var dist = new Dictionary<string, double>();
            var open = Data.Edges.Where(e => !_blocked.Contains(e.Id)).ToList();
            foreach (var e in open) dist[e.Id] = double.PositiveInfinity;
            var heap = new MinHeap<string>();
            foreach (var e in open)
            {
                if (IsExit(e.From) || IsExit(e.To))
                {
                    dist[e.Id] = 0;
                    heap.Push(0, e.Id);
                }
            }
            while (heap.Count > 0)
            {
                var (d, eid) = heap.Pop();
                if (d > dist[eid]) continue;
                var e = _edges[eid];
                foreach (var zid in new[] { e.From, e.To })
                {
                    if (IsExit(zid)) continue;
                    foreach (var n in _edgesOf[zid])
                    {
                        if (n.Id == eid || _blocked.Contains(n.Id)) continue;
                        double nd = d + Dist(e.Center, n.Center);
                        if (nd < dist[n.Id])
                        {
                            dist[n.Id] = nd;
                            heap.Push(nd, n.Id);
                        }
                    }
                }
            }
            return dist;
        }

        // ── paths for NPCs ─────────────────────────────────────────────────────

        /// <summary>Waypoints (edge centres, then the exit zone's target point) for NPC walking. Empty if unreachable or outside.</summary>
        public IReadOnlyList<Vec3> PathToExit(Vec3 from) =>
            FindPath(from, zid => IsExit(zid));

        /// <summary>Waypoints (edge centres, then the zone's spawn or polygon centroid). Empty if unreachable or outside.</summary>
        public IReadOnlyList<Vec3> PathTo(Vec3 from, string zoneId)
        {
            Zone(zoneId);
            return FindPath(from, zid => zid == zoneId);
        }

        /// <summary>Where an NPC stops inside a zone: its spawn if set, else the polygon centroid (y = floor).</summary>
        internal Vec3 TargetPoint(string zoneId)
        {
            var z = Zone(zoneId);
            if (z.Spawn.HasValue) return z.Spawn.Value;
            var c = Centroid(z.Polygon);
            return new Vec3(c.X, 0, c.Z);
        }

        private IReadOnlyList<Vec3> FindPath(Vec3 from, Func<string, bool> isGoal)
        {
            string start = ZoneAt(from);
            if (start == null) return Array.Empty<Vec3>();
            if (isGoal(start)) return new[] { Floor(TargetPoint(start), from) };

            // Dijkstra over states (edge crossed, zone entered). Cost = straight lines between edge centres.
            var dist = new Dictionary<(string, string), double>();
            var prev = new Dictionary<(string, string), (string, string)?>();
            var heap = new MinHeap<(string edge, string zone)>();
            foreach (var e in _edgesOf[start])
            {
                if (_blocked.Contains(e.Id)) continue;
                var s = (e.Id, Other(e, start));
                double d = Dist(from.X, from.Z, e.Center);
                if (!dist.TryGetValue(s, out var old) || d < old) { dist[s] = d; prev[s] = null; heap.Push(d, s); }
            }

            double bestTotal = double.PositiveInfinity;
            (string, string)? bestState = null;
            while (heap.Count > 0)
            {
                var (d, s) = heap.Pop();
                if (d > dist[s] || d >= bestTotal) continue;
                var e = _edges[s.edge];
                if (isGoal(s.zone))
                {
                    var tp = TargetPoint(s.zone);
                    double total = d + Dist(tp.X, tp.Z, e.Center);
                    if (total < bestTotal) { bestTotal = total; bestState = s; }
                    continue;
                }
                if (IsExit(s.zone)) continue; // exits vent outside; do not route through them
                foreach (var n in _edgesOf[s.zone])
                {
                    if (n.Id == s.edge || _blocked.Contains(n.Id)) continue;
                    var ns = (n.Id, Other(n, s.zone));
                    double nd = d + Dist(e.Center, n.Center);
                    if (!dist.TryGetValue(ns, out var old) || nd < old) { dist[ns] = nd; prev[ns] = s; heap.Push(nd, ns); }
                }
            }
            if (bestState == null) return Array.Empty<Vec3>();

            var points = new List<Vec3> { Floor(TargetPoint(bestState.Value.Item2), from) };
            for ((string, string)? cur = bestState; cur != null; cur = prev[cur.Value])
            {
                var c = _edges[cur.Value.Item1].Center;
                points.Add(new Vec3(c.X, from.Y, c.Z));
            }
            points.Reverse();
            return points;
        }

        private static Vec3 Floor(Vec3 p, Vec3 from) => new Vec3(p.X, from.Y, p.Z);

        // ── geometry ───────────────────────────────────────────────────────────

        private static double Dist(Vec2 a, Vec2 b) => Dist(a.X, a.Z, b);
        private static double Dist(double x, double z, Vec2 b)
        {
            double dx = x - b.X, dz = z - b.Z;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        internal static double PolygonArea(IReadOnlyList<Vec2> poly)
        {
            double s = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                s += (double)a.X * b.Z - (double)b.X * a.Z;
            }
            return Math.Abs(s) / 2;
        }

        private static Vec2 Centroid(IReadOnlyList<Vec2> poly)
        {
            double a = 0, cx = 0, cz = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i];
                var q = poly[(i + 1) % poly.Count];
                double cross = (double)p.X * q.Z - (double)q.X * p.Z;
                a += cross;
                cx += (p.X + q.X) * cross;
                cz += (p.Z + q.Z) * cross;
            }
            if (Math.Abs(a) < 1e-12) return new Vec2((float)poly.Average(p => p.X), (float)poly.Average(p => p.Z));
            return new Vec2((float)(cx / (3 * a)), (float)(cz / (3 * a)));
        }

        private enum Containment { Outside, Boundary, Inside }

        private static Containment Contains(IReadOnlyList<Vec2> poly, double x, double z)
        {
            const double eps = 1e-6;
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double xi = poly[i].X, zi = poly[i].Z, xj = poly[j].X, zj = poly[j].Z;
                // on segment?
                double cross = (xj - xi) * (z - zi) - (zj - zi) * (x - xi);
                if (Math.Abs(cross) < eps
                    && x >= Math.Min(xi, xj) - eps && x <= Math.Max(xi, xj) + eps
                    && z >= Math.Min(zi, zj) - eps && z <= Math.Max(zi, zj) + eps)
                    return Containment.Boundary;
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside ? Containment.Inside : Containment.Outside;
        }
    }

    /// <summary>Small binary heap keyed by double (Unity's .NET profile has no PriorityQueue).</summary>
    internal sealed class MinHeap<T>
    {
        private readonly List<(double key, long order, T value)> _items = new List<(double, long, T)>();
        private long _counter;
        public int Count => _items.Count;

        public void Push(double key, T value)
        {
            _items.Add((key, _counter++, value));
            int i = _items.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (!Less(_items[i], _items[p])) break;
                (_items[i], _items[p]) = (_items[p], _items[i]);
                i = p;
            }
        }

        public (double key, T value) Pop()
        {
            var top = _items[0];
            int last = _items.Count - 1;
            _items[0] = _items[last];
            _items.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < _items.Count && Less(_items[l], _items[m])) m = l;
                if (r < _items.Count && Less(_items[r], _items[m])) m = r;
                if (m == i) break;
                (_items[i], _items[m]) = (_items[m], _items[i]);
                i = m;
            }
            return (top.key, top.value);
        }

        private static bool Less((double key, long order, T) a, (double key, long order, T) b) =>
            a.key < b.key || (a.key == b.key && a.order < b.order);
    }
}
