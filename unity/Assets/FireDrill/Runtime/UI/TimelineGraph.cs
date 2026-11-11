using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireDrill.Runtime
{
    /// <summary>16.11 report timeline: head-height smoke concentration c over run time, with a marker and the zone
    /// name wherever the trainee moved to another zone. Drawn with Painter2D.</summary>
    public sealed class TimelineGraph : VisualElement
    {
        private const float Pad = 26f;
        private IReadOnlyList<TimelinePoint> _points = new List<TimelinePoint>();
        private float _maxC = 1f;
        private float? _fireT;
        private ZoneGraph _graph;
        private readonly List<Label> _labels = new List<Label>();

        public TimelineGraph()
        {
            style.flexGrow = 1;
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => PlaceLabels());
        }

        public void SetData(IReadOnlyList<TimelinePoint> points, float maxC, ZoneGraph graph, float? fireT)
        {
            _points = points ?? new List<TimelinePoint>();
            _maxC = Mathf.Max(maxC, 1e-3f);
            _graph = graph;
            _fireT = fireT;
            foreach (var l in _labels) l.RemoveFromHierarchy();
            _labels.Clear();
            foreach (var (t, zone) in ZoneChanges())
            {
                var label = new Label(ZoneName(zone)) { userData = t };
                label.AddToClassList("timeline-label");
                Add(label);
                _labels.Add(label);
            }
            PlaceLabels();
            MarkDirtyRepaint();
        }

        private IEnumerable<(float t, string zone)> ZoneChanges()
        {
            string last = null;
            foreach (var p in _points)
            {
                if (p.Zone == null || p.Zone == last) continue;
                last = p.Zone;
                yield return (p.T, p.Zone);
            }
        }

        private string ZoneName(string zone)
        {
            var z = _graph?.Zones.FirstOrDefault(x => x.Id == zone);
            return string.IsNullOrEmpty(z?.Name) ? zone : z.Name;
        }

        private float TMax => _points.Count > 0 ? Mathf.Max(1f, _points[_points.Count - 1].T) : 1f;
        private float X(float t, Rect r) => r.xMin + Pad + (r.width - 2 * Pad) * (t / TMax);
        private float Y(float c, Rect r) => r.yMax - Pad - (r.height - 2 * Pad) * Mathf.Clamp01(c / _maxC);

        private void PlaceLabels()
        {
            var r = contentRect;
            if (r.width <= 0) return;
            for (int i = 0; i < _labels.Count; i++)
            {
                var l = _labels[i];
                l.style.left = X((float)l.userData, r) + 3;
                l.style.top = 2 + (i % 2) * 16; // alternate rows so neighbouring names do not overlap
            }
        }

        private void Draw(MeshGenerationContext ctx)
        {
            var r = contentRect;
            if (r.width <= 0 || r.height <= 0) return;
            var p = ctx.painter2D;

            p.strokeColor = new Color(1, 1, 1, 0.25f);
            p.lineWidth = 1f;
            p.BeginPath();
            p.MoveTo(new Vector2(r.xMin + Pad, r.yMin + Pad / 2));
            p.LineTo(new Vector2(r.xMin + Pad, r.yMax - Pad));
            p.LineTo(new Vector2(r.xMax - Pad, r.yMax - Pad));
            p.Stroke();

            p.strokeColor = new Color(1, 1, 1, 0.18f);
            foreach (var (t, _) in ZoneChanges())
            {
                p.BeginPath();
                p.MoveTo(new Vector2(X(t, r), r.yMin + Pad / 2));
                p.LineTo(new Vector2(X(t, r), r.yMax - Pad));
                p.Stroke();
            }
            if (_fireT.HasValue)
            {
                p.strokeColor = new Color(1f, 0.5f, 0.3f, 0.7f);
                p.BeginPath();
                p.MoveTo(new Vector2(X(_fireT.Value, r), r.yMin + Pad / 2));
                p.LineTo(new Vector2(X(_fireT.Value, r), r.yMax - Pad));
                p.Stroke();
            }

            if (_points.Count < 2) return;
            p.strokeColor = new Color(1f, 0.62f, 0.3f, 1f);
            p.lineWidth = 3f;
            p.lineJoin = LineJoin.Round;
            p.BeginPath();
            p.MoveTo(new Vector2(X(_points[0].T, r), Y(_points[0].CHead, r)));
            for (int i = 1; i < _points.Count; i++) p.LineTo(new Vector2(X(_points[i].T, r), Y(_points[i].CHead, r)));
            p.Stroke();
        }
    }
}
