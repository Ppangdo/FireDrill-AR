using System;
using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using UnityEngine;

namespace FireDrill.Runtime
{
    /// <summary>Builds the PC building from zone_graph.json at run time (명세서 16.2): floors, ceilings, walls with
    /// door/opening gaps, doors, exit markings, placeholder objects and one ceiling light per zone.
    /// Building coordinates are world coordinates on PC.</summary>
    public static class SceneBuilder
    {
        private const float Eps = 1e-3f;

        public static BuildingView Build(ZoneGraph graph, RuntimeConfig.SceneSettings cfg, Transform parent = null, Font signFont = null)
        {
            var root = new GameObject("Building_" + graph.Data.BuildingId);
            if (parent != null) root.transform.SetParent(parent, false);
            var view = root.AddComponent<BuildingView>();
            var mats = new MaterialCache(cfg);

            foreach (var zone in graph.Zones) BuildZone(view, zone, cfg, mats, signFont);
            BuildWalls(view, graph, cfg, mats);
            foreach (var edge in graph.Edges.Where(e => e.Type == EdgeType.Door)) BuildDoor(view, graph, edge, cfg, mats);
            foreach (var edge in graph.Edges)
            {
                BuildExitSign(view, graph, edge, cfg, mats);
                if (edge.Type == EdgeType.Door) BuildRoomSign(view, graph, edge, cfg, mats, signFont);
            }
            return view;
        }

        // ---- floors, ceilings, lights, objects -------------------------------

        private static void BuildZone(BuildingView view, Zone zone, RuntimeConfig.SceneSettings cfg, MaterialCache mats, Font signFont)
        {
            var go = new GameObject("Zone_" + zone.Id);
            go.transform.SetParent(view.transform, false);
            var pts = zone.Polygon.Select(p => new Vector2(p.X, p.Z)).ToList();
            var tris = Triangulate(pts);

            var floor = MeshObject("Floor", go.transform, FloorMesh(pts, tris, 0f, up: true),
                                   mats.Get(cfg.Colors[ColorKey(zone.Type)]));
            floor.AddComponent<MeshCollider>();
            var ceiling = MeshObject("Ceiling", go.transform, FloorMesh(pts, tris, zone.CeilingH, up: false),
                                     mats.Get(cfg.Colors["ceiling"]));
            ceiling.AddComponent<MeshCollider>();

            var centroid = new Vector3(pts.Average(p => p.x), zone.CeilingH - 0.1f, pts.Average(p => p.y));
            var lightGo = new GameObject("Light");
            lightGo.transform.SetParent(go.transform, false);
            lightGo.transform.position = centroid;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.intensity = cfg.LightIntensity;
            light.range = cfg.LightRangeM;

            view.Zones[zone.Id] = new ZoneView(zone.Id, floor, ceiling, light);

            foreach (var obj in zone.Objects ?? new List<BuildingObject>())
            {
                var kind = Snake(obj.Kind.ToString());
                if (!cfg.Objects.TryGetValue(kind, out var shape)) continue;
                var prim = GameObject.CreatePrimitive(shape.Shape == "cylinder" ? PrimitiveType.Cylinder : PrimitiveType.Cube);
                prim.name = obj.Id;
                prim.transform.SetParent(go.transform, false);
                prim.transform.position = ToWorld(obj.Pos);
                // Unity's cylinder primitive is 2 m tall: halve y so size_m is the real height.
                prim.transform.localScale = new Vector3(shape.SizeM[0], shape.Shape == "cylinder" ? shape.SizeM[1] / 2f : shape.SizeM[1], shape.SizeM[2]);
                prim.GetComponent<Renderer>().sharedMaterial = mats.Get(shape.Color);
                var target = prim.AddComponent<InteractTarget>();
                target.TargetId = obj.Id;
                view.Objects[obj.Id] = new ObjectView(obj.Id, obj.Kind, prim);
                if (shape.Pickable) BuildItemStand(go.transform, obj, shape, cfg, mats);
                if (obj.Kind == ObjectKind.Faucet)
                    BuildFaucetDetails(prim, ToWorld(obj.Pos), centroid, cfg, mats, signFont);
            }
        }

        private static void BuildItemStand(Transform parent, BuildingObject obj, RuntimeConfig.ObjectShape shape,
                                           RuntimeConfig.SceneSettings cfg, MaterialCache mats)
        {
            float top = obj.Pos.Y - shape.SizeM[1] / 2f;
            if (top <= Eps) return;

            var stand = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stand.name = "Stand_" + obj.Id;
            stand.transform.SetParent(parent, false);
            stand.transform.position = new Vector3(obj.Pos.X, top / 2f, obj.Pos.Z);
            stand.transform.localScale = new Vector3(shape.SizeM[0] + cfg.WallThicknessM, top,
                                                     shape.SizeM[2] + cfg.WallThicknessM);
            stand.GetComponent<Renderer>().sharedMaterial = mats.Get(cfg.Colors["door"]);
        }

        private static void BuildFaucetDetails(GameObject faucet, Vector3 position, Vector3 zoneCenter,
                                               RuntimeConfig.SceneSettings cfg, MaterialCache mats, Font font)
        {
            var towardRoom = zoneCenter - position;
            towardRoom.y = 0f;
            towardRoom.Normalize();
            var size = faucet.transform.localScale;
            var fixture = mats.Get(cfg.Colors["handle"]);
            var parent = faucet.transform.parent;
            var spoutBase = position - towardRoom * (size.x / 4f) + Vector3.up * (size.y * 0.7f);
            DecorativeBox("TapStem", parent, spoutBase, new Vector3(size.z / 6f, size.y / 2f, size.z / 6f),
                          Quaternion.identity, fixture);
            DecorativeBox("TapSpout", parent, spoutBase + Vector3.up * (size.y / 4f) + towardRoom * (size.x / 8f),
                          new Vector3(size.z / 6f, size.y / 5f, size.x / 3f), Quaternion.LookRotation(towardRoom), fixture);
            var labelPos = position + Vector3.up * size.y + towardRoom * (size.x / 2f);
            BuildPlaque("WaterSign", font != null ? "수도" : "WATER", parent, labelPos, towardRoom, cfg, mats, font);
        }

        private static string ColorKey(ZoneType t) => t switch
        {
            ZoneType.Corridor => "corridor",
            ZoneType.ElevatorLobby => "elevator_lobby",
            ZoneType.Exit => "exit",
            ZoneType.Stair => "corridor",
            ZoneType.Refuge => "corridor",
            _ => "room",
        };

        // ---- walls -----------------------------------------------------------

        private struct Seg
        {
            public Vector2 A, B;
            public float Height;
            public float Length => Vector2.Distance(A, B);
            public Vector2 Dir => (B - A).normalized;
        }

        private static void BuildWalls(BuildingView view, ZoneGraph graph, RuntimeConfig.SceneSettings cfg, MaterialCache mats)
        {
            var parent = new GameObject("Walls").transform;
            parent.SetParent(view.transform, false);
            var placed = new List<Seg>();
            var wallMat = mats.Get(cfg.Colors["wall"]);

            foreach (var zone in graph.Zones)
            {
                var poly = zone.Polygon;
                for (int i = 0; i < poly.Count; i++)
                {
                    var seg = new Seg
                    {
                        A = new Vector2(poly[i].X, poly[i].Z),
                        B = new Vector2(poly[(i + 1) % poly.Count].X, poly[(i + 1) % poly.Count].Z),
                        Height = zone.CeilingH,
                    };
                    if (seg.Length < Eps) continue;

                    // Intervals (in metres along the segment) still to build: minus shared walls, minus gaps.
                    var keep = new List<(float a, float b)> { (0f, seg.Length) };
                    foreach (var other in placed) keep = Subtract(keep, Overlap(seg, other));
                    var lintels = new List<(float a, float b)>();
                    foreach (var edge in graph.Edges)
                    {
                        var gap = GapOn(seg, edge);
                        if (gap == null) continue;
                        keep = Subtract(keep, gap.Value);
                        if (edge.Type == EdgeType.Door && cfg.DoorTopM < seg.Height) lintels.Add(gap.Value);
                    }
                    placed.Add(seg);

                    foreach (var (a, b) in keep)
                        if (b - a > Eps) view.Walls.Add(WallBox(parent, seg, a, b, 0f, seg.Height, cfg.WallThicknessM, wallMat));
                    foreach (var (a, b) in lintels)
                        view.Walls.Add(WallBox(parent, seg, a, b, cfg.DoorTopM, seg.Height, cfg.WallThicknessM, wallMat));
                }
            }
        }

        /// <summary>Part of <paramref name="seg"/> covered by a collinear <paramref name="other"/>, or null.</summary>
        private static (float a, float b)? Overlap(Seg seg, Seg other)
        {
            var d = seg.Dir;
            if (Mathf.Abs(Cross(d, other.Dir)) > Eps) return null;
            if (Mathf.Abs(Cross(d, other.A - seg.A)) > Eps) return null;
            float t0 = Vector2.Dot(other.A - seg.A, d), t1 = Vector2.Dot(other.B - seg.A, d);
            float lo = Mathf.Max(0, Mathf.Min(t0, t1)), hi = Mathf.Min(seg.Length, Mathf.Max(t0, t1));
            return hi - lo > Eps ? (lo, hi) : ((float, float)?)null;
        }

        /// <summary>Gap an edge (door or opening) cuts into <paramref name="seg"/>, or null when not on it.</summary>
        private static (float a, float b)? GapOn(Seg seg, Edge edge)
        {
            var c = new Vector2(edge.Center.X, edge.Center.Z);
            var d = seg.Dir;
            if (Mathf.Abs(Cross(d, c - seg.A)) > Eps) return null;
            float t = Vector2.Dot(c - seg.A, d);
            if (t < -Eps || t > seg.Length + Eps) return null;
            float h = edge.Width / 2f;
            return (Mathf.Max(0, t - h), Mathf.Min(seg.Length, t + h));
        }

        private static List<(float a, float b)> Subtract(List<(float a, float b)> keep, (float a, float b)? cut)
        {
            if (cut == null) return keep;
            var (ca, cb) = cut.Value;
            var result = new List<(float a, float b)>();
            foreach (var (a, b) in keep)
            {
                if (cb <= a || ca >= b) { result.Add((a, b)); continue; }
                if (ca > a) result.Add((a, ca));
                if (cb < b) result.Add((cb, b));
            }
            return result;
        }

        private static GameObject WallBox(Transform parent, Seg seg, float a, float b, float y0, float y1, float thickness, Material mat)
        {
            var d = seg.Dir;
            var mid = seg.A + d * ((a + b) / 2f);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall";
            wall.transform.SetParent(parent, false);
            wall.transform.position = new Vector3(mid.x, (y0 + y1) / 2f, mid.y);
            wall.transform.rotation = Quaternion.FromToRotation(Vector3.right, new Vector3(d.x, 0, d.y));
            wall.transform.localScale = new Vector3(b - a, y1 - y0, thickness);
            wall.GetComponent<Renderer>().sharedMaterial = mat;
            return wall;
        }

        // ---- doors and exit signs --------------------------------------------

        private static void BuildDoor(BuildingView view, ZoneGraph graph, Edge edge, RuntimeConfig.SceneSettings cfg, MaterialCache mats)
        {
            var center = new Vector3(edge.Center.X, 0, edge.Center.Z);
            var along = edge.WallAxis == WallAxis.X ? Vector3.right : Vector3.forward;
            var normal = new Vector3(along.z, 0, -along.x);
            // Swing into the "from" zone.
            var probe = center + normal * 0.3f;
            if (graph.ZoneAt(new Vec3(probe.x, 0, probe.z)) != edge.From) normal = -normal;
            // Hinge on the end away from the handle (both-sided handles: the handle point still picks the latch side).
            var handle = edge.Handle.HasValue ? ToWorld(edge.Handle.Value) : center + along * (edge.Width / 2f);
            var latchDir = Vector3.Dot(handle - center, along) >= 0 ? along : -along;
            var hinge = center - latchDir * (edge.Width / 2f);

            var pivot = new GameObject("Door_" + edge.Id);
            pivot.transform.SetParent(view.transform, false);
            pivot.transform.position = hinge;
            var closed = Quaternion.FromToRotation(Vector3.right, latchDir);
            var open = Quaternion.FromToRotation(latchDir, normal) * closed;

            var leaf = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leaf.name = "Leaf";
            leaf.transform.SetParent(pivot.transform, false);
            leaf.transform.localPosition = new Vector3(edge.Width / 2f, cfg.DoorTopM / 2f, 0);
            leaf.transform.localScale = new Vector3(edge.Width, cfg.DoorTopM, cfg.DoorThicknessM);
            leaf.GetComponent<Renderer>().sharedMaterial = mats.Get(cfg.Colors["door"]);
            leaf.AddComponent<InteractTarget>().TargetId = edge.Id;

            var knob = GameObject.CreatePrimitive(PrimitiveType.Cube);
            knob.name = "Handle";
            knob.transform.SetParent(pivot.transform, false);
            pivot.transform.rotation = closed;
            knob.transform.position = new Vector3(handle.x, edge.Handle?.Y ?? 1f, handle.z);
            knob.transform.localRotation = Quaternion.identity;
            // handle_size_m: x = length through the leaf (both sides), y/z = cross-section.
            knob.transform.localScale = new Vector3(cfg.HandleSizeM[1], cfg.HandleSizeM[2], cfg.HandleSizeM[0]);
            knob.GetComponent<Renderer>().sharedMaterial = mats.Get(cfg.Colors["handle"]);
            UnityEngine.Object.Destroy(knob.GetComponent<Collider>());

            var door = pivot.AddComponent<DoorView>();
            door.Init(edge.Id, closed, open, cfg.DoorSwingS, leaf.GetComponent<Collider>());
            door.SetOpen(edge.DoorOpen ?? false, instant: true);
            view.Doors[edge.Id] = door;
        }

        private static void BuildExitSign(BuildingView view, ZoneGraph graph, Edge edge, RuntimeConfig.SceneSettings cfg, MaterialCache mats)
        {
            bool fromExit = graph.Zone(edge.From).Type == ZoneType.Exit, toExit = graph.Zone(edge.To).Type == ZoneType.Exit;
            if (fromExit == toExit) return;
            var inside = fromExit ? edge.To : edge.From; // the sign faces the zone you leave from
            var along = edge.WallAxis == WallAxis.X ? Vector3.right : Vector3.forward;
            var normal = new Vector3(along.z, 0, -along.x);
            var center = new Vector3(edge.Center.X, 0, edge.Center.Z);
            var probe = center + normal * 0.3f;
            if (graph.ZoneAt(new Vec3(probe.x, 0, probe.z)) != inside) normal = -normal;

            var sign = GameObject.CreatePrimitive(PrimitiveType.Cube);
            sign.name = "ExitSign_" + edge.Id;
            sign.transform.SetParent(view.transform, false);
            sign.transform.position = center + normal * (cfg.WallThicknessM / 2f + cfg.ExitSignSizeM[2] / 2f) + Vector3.up * cfg.ExitSignHeightM;
            sign.transform.rotation = Quaternion.FromToRotation(Vector3.right, along);
            sign.transform.localScale = new Vector3(cfg.ExitSignSizeM[0], cfg.ExitSignSizeM[1], cfg.ExitSignSizeM[2]);
            sign.GetComponent<Renderer>().sharedMaterial = mats.Get(cfg.Colors["exit_sign"], unlit: true);
            UnityEngine.Object.Destroy(sign.GetComponent<Collider>());
            view.ExitSigns.Add(sign);
        }

        private static void BuildRoomSign(BuildingView view, ZoneGraph graph, Edge edge, RuntimeConfig.SceneSettings cfg,
                                          MaterialCache mats, Font font)
        {
            var room = graph.Zone(edge.From);
            string facing = edge.To;
            if (room.Type != ZoneType.Room)
            {
                room = graph.Zone(edge.To);
                facing = edge.From;
            }
            if (room.Type != ZoneType.Room) return;

            string number = new string(room.Name.TakeWhile(char.IsDigit).ToArray());
            if (number.Length == 0) return;
            var center = new Vector3(edge.Center.X, 0, edge.Center.Z);
            var normal = edge.WallAxis == WallAxis.X ? Vector3.forward : Vector3.right;
            var probe = center + normal * cfg.WallThicknessM;
            if (graph.ZoneAt(new Vec3(probe.x, 0f, probe.z)) != facing) normal = -normal;
            var position = center + normal * (cfg.WallThicknessM + cfg.ExitSignSizeM[2]) +
                           Vector3.up * cfg.ExitSignHeightM;
            BuildPlaque("RoomSign_" + number, number, view.transform, position, normal, cfg, mats, font);
        }

        private static void BuildPlaque(string name, string caption, Transform parent, Vector3 position, Vector3 normal,
                                        RuntimeConfig.SceneSettings cfg, MaterialCache mats, Font font)
        {
            var size = new Vector3(cfg.ExitSignSizeM[0], cfg.ExitSignSizeM[1], cfg.ExitSignSizeM[2]);
            var rotation = Quaternion.LookRotation(-normal, Vector3.up);
            DecorativeBox(name, parent, position, size, rotation, mats.Get("#18334D", unlit: true));
            var label = new GameObject(name + "_Label");
            label.transform.SetParent(parent, false);
            label.transform.SetPositionAndRotation(position + normal * (size.z / 2f + Eps), rotation);
            var text = label.AddComponent<TextMesh>();
            text.text = caption;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 48;
            text.characterSize = size.y / 3f;
            SetWorldTextFont(text, font);
        }

        private static void DecorativeBox(string name, Transform parent, Vector3 position, Vector3 size,
                                          Quaternion rotation, Material material)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.SetPositionAndRotation(position, rotation);
            box.transform.localScale = size;
            box.GetComponent<Renderer>().sharedMaterial = material;
            box.GetComponent<Collider>().enabled = false;
            UnityEngine.Object.Destroy(box.GetComponent<Collider>());
        }

        /// <summary>Use the font atlas with a material that respects scene depth.</summary>
        public static void SetWorldTextFont(TextMesh text, Font font)
        {
            text.font = font ?? text.font ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var material = text.font.material;
            var shader = Resources.Load<Shader>(UiPanel.ResourceDir + "WorldText");
            if (shader != null) material.shader = shader;
            text.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        // ---- meshes ----------------------------------------------------------

        private static GameObject MeshObject(string name, Transform parent, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        private static Mesh FloorMesh(List<Vector2> pts, List<int> tris, float y, bool up)
        {
            var mesh = new Mesh { name = up ? "floor" : "ceiling" };
            mesh.SetVertices(pts.Select(p => new Vector3(p.x, y, p.y)).ToList());
            mesh.SetUVs(0, pts.ToList());
            var idx = new List<int>(tris);
            // Triangulate returns counter-clockwise triangles in (x, z). Unity front faces are clockwise seen
            // from the viewer, so seen from above that is a ceiling (faces down); flip for floors (face up).
            if (up)
                for (int i = 0; i < idx.Count; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
            mesh.SetTriangles(idx, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Ear clipping for simple polygons (x = building x, y = building z).</summary>
        public static List<int> Triangulate(IReadOnlyList<Vector2> pts)
        {
            var result = new List<int>();
            var idx = Enumerable.Range(0, pts.Count).ToList();
            if (SignedArea(pts) < 0) idx.Reverse(); // work counter-clockwise
            int guard = 0;
            while (idx.Count > 3 && guard++ < 10000)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int a = idx[(i + idx.Count - 1) % idx.Count], b = idx[i], c = idx[(i + 1) % idx.Count];
                    if (Cross(pts[b] - pts[a], pts[c] - pts[b]) <= 0) continue; // reflex
                    bool contains = false;
                    foreach (int j in idx)
                        if (j != a && j != b && j != c && InTriangle(pts[j], pts[a], pts[b], pts[c])) { contains = true; break; }
                    if (contains) continue;
                    result.AddRange(new[] { a, b, c });
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (idx.Count == 3) result.AddRange(idx);
            return result;
        }

        private static float SignedArea(IReadOnlyList<Vector2> p)
        {
            float s = 0;
            for (int i = 0; i < p.Count; i++) s += Cross(p[i], p[(i + 1) % p.Count]);
            return s / 2f;
        }

        private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c) =>
            Cross(b - a, p - a) >= 0 && Cross(c - b, p - b) >= 0 && Cross(a - c, p - c) >= 0;

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        public static Vector3 ToWorld(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
        public static Vec3 ToBuilding(Vector3 v) => new Vec3(v.x, v.y, v.z);

        private static string Snake(string pascal) =>
            string.Concat(pascal.Select((ch, i) => i > 0 && char.IsUpper(ch) ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));

        private sealed class MaterialCache
        {
            private readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();
            private readonly Shader _lit, _unlit;

            public MaterialCache(RuntimeConfig.SceneSettings cfg)
            {
                _lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")
                       ?? Shader.Find("Hidden/InternalErrorShader");
                _unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? _lit;
            }

            public Material Get(string hex, bool unlit = false)
            {
                var key = hex + (unlit ? "u" : "l");
                if (_mats.TryGetValue(key, out var m)) return m;
                if (!ColorUtility.TryParseHtmlString(hex, out var color)) throw new ArgumentException("bad color " + hex);
                m = new Material(unlit ? _unlit : _lit) { name = key, color = color };
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
                return _mats[key] = m;
            }
        }
    }
}
