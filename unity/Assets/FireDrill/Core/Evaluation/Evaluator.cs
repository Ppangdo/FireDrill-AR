using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace FireDrill.Core
{
    /// <summary>
    /// Pure rule-based scoring (명세서 7장). Same log → same result. Each item follows the `measure`
    /// sentence in evaluation.json; the reference is contracts/tools/ref_eval.py and the golden
    /// expectations in contracts/golden/*.expected.json.
    /// total_pct = floor(100 × Σpoints / Σmax over non-na items + 0.5), 0 if every item is na.
    /// </summary>
    public static class Evaluator
    {
        public static EvaluationResult Evaluate(IReadOnlyList<LogEvent> events, ZoneGraph graph, Scenario scenario, EvaluationConfig cfg)
        {
            if (events == null) throw new ArgumentNullException(nameof(events));
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            var ctx = new Context(events, graph, scenario, cfg);
            var outcomes = new Dictionary<string, (Grade grade, JObject evidence)>
            {
                ["alert"] = Alert(ctx),
                ["evac_delay"] = EvacDelay(ctx),
                ["no_elevator"] = NoElevator(ctx),
                ["door_check"] = DoorCheck(ctx),
                ["nose_mouth"] = NoseMouth(ctx),
                ["low_posture"] = LowPosture(ctx),
                // Not implemented in the PC demo (16.14); mvp_only also forces na.
                ["extinguish"] = (Grade.Na, new JObject()),
                ["blocked_response"] = (Grade.Na, new JObject()),
                ["belongings"] = Belongings(ctx),
                ["warn_others"] = WarnOthers(ctx),
            };

            var result = new EvaluationResult
            {
                RunId = events.Count > 0 ? events[0].RunId : null,
                Outcome = OutcomeOf(events),
                ElapsedS = ElapsedOf(events),
            };

            double pts = 0, max = 0;
            foreach (var item in cfg.Items)
            {
                var (grade, evidence) = outcomes.TryGetValue(item.Id, out var o) ? o : (Grade.Na, new JObject());
                if (cfg.MvpOnly && !item.Mvp) grade = Grade.Na;
                float points = grade == Grade.Na ? 0f : item.Weight * Ratio(cfg, grade);
                if (grade != Grade.Na)
                {
                    pts += points;
                    max += item.Weight;
                }
                result.Items.Add(new ItemResult
                {
                    Id = item.Id, Grade = grade, Points = points, Max = item.Weight, Evidence = evidence, RuleRef = item.RuleRef,
                });
            }
            result.TotalPct = max > 0 ? (int)Math.Floor(100.0 * pts / max + 0.5) : 0;

            foreach (var e in events.Where(e => e.Type == EventTypes.Sample))
                result.Timeline.Add(new TimelinePoint { T = e.T, CHead = e.CHead, Zone = e.Zone });
            result.WeakZones = WeakZones(ctx);
            return result;
        }

        // ── shared state ───────────────────────────────────────────────────────

        private sealed class Context
        {
            public readonly IReadOnlyList<LogEvent> Events;
            public readonly ZoneGraph Graph;
            public readonly Scenario Scenario;
            public readonly EvaluationConfig Cfg;
            public readonly Dictionary<string, EvaluationConfig.Item> Items;
            /// <summary>t of the first CUE_EXPOSED, or null.</summary>
            public readonly float? T0;
            /// <summary>Events with t ≥ t0 (empty without t0).</summary>
            public readonly List<LogEvent> After;
            public readonly List<LogEvent> Samples;

            public Context(IReadOnlyList<LogEvent> events, ZoneGraph graph, Scenario scenario, EvaluationConfig cfg)
            {
                Events = events; Graph = graph; Scenario = scenario; Cfg = cfg;
                Items = cfg.Items.ToDictionary(i => i.Id);
                var cue = events.FirstOrDefault(e => e.Type == EventTypes.CueExposed);
                T0 = cue?.T;
                After = T0 == null ? new List<LogEvent>() : events.Where(e => e.T >= T0.Value).ToList();
                Samples = After.Where(e => e.Type == EventTypes.Sample).ToList();
            }

            public float Th(string item, string key)
            {
                if (!Items.TryGetValue(item, out var it)) throw new KeyNotFoundException($"evaluation.json has no item \"{item}\"");
                if (it.Thresholds == null || !it.Thresholds.TryGetValue(key, out var v))
                    throw new KeyNotFoundException($"evaluation.json item \"{item}\" has no threshold \"{key}\"");
                return v;
            }
        }

        private static float Ratio(EvaluationConfig cfg, Grade g) =>
            cfg.GradeRatio[g == Grade.Good ? "good" : g == Grade.Normal ? "normal" : "poor"];

        private static Grade ByTime(double? value, double goodMax, double normalMax) =>
            value == null ? Grade.Poor : value <= goodMax ? Grade.Good : value <= normalMax ? Grade.Normal : Grade.Poor;

        private static Grade ByRatioMin(double ratio, double goodMin, double normalMin) =>
            ratio >= goodMin ? Grade.Good : ratio >= normalMin ? Grade.Normal : Grade.Poor;

        private static JToken Round(double? v, int digits) => v == null ? JValue.CreateNull() : new JValue(Math.Round(v.Value, digits));

        private static string Str(LogEvent e, string key) => (string)e.Data[key];
        private static bool Bool(LogEvent e, string key) => e.Data[key] != null && e.Data[key].Type == JTokenType.Boolean && (bool)e.Data[key];

        // ── items ──────────────────────────────────────────────────────────────

        private static (Grade, JObject) Alert(Context c)
        {
            if (c.T0 == null) return (Grade.Na, new JObject());
            var hit = c.After.FirstOrDefault(e => e.Type == EventTypes.AlarmPressed || e.Type == EventTypes.Shout);
            double? delay = hit == null ? (double?)null : (double)hit.T - c.T0.Value;
            return (ByTime(delay, c.Th("alert", "good_max_s"), c.Th("alert", "normal_max_s")),
                    new JObject { ["delay_s"] = Round(delay, 1) });
        }

        private static (Grade, JObject) EvacDelay(Context c)
        {
            if (c.T0 == null) return (Grade.Na, new JObject());
            double firstDrop = c.Th("evac_delay", "first_step_drop_m");
            double window = c.Th("evac_delay", "window_s");
            double maxRise = c.Th("evac_delay", "max_rise_m");
            double minDrop = c.Th("evac_delay", "min_drop_m");

            var pts = new List<(double t, double? e, bool isExit)>();
            foreach (var ev in c.After)
            {
                if (ev.Type == EventTypes.Sample)
                    pts.Add((ev.T, c.Graph.ExitDistance(ev.Zone, ev.Pos), false));
                else if (ev.Type == EventTypes.ZoneEnter && IsExitZone(c.Graph, Str(ev, "to")))
                    pts.Add((ev.T, 0.0, true));
            }

            double? start = null;
            for (int i = 0; i < pts.Count - 1 && start == null; i++)
            {
                var (ts, es, _) = pts[i];
                var en = pts[i + 1].e;
                if (es == null || en == null || !(en < es - firstDrop)) continue;
                bool ok = true, reached = false;
                double prev = es.Value, last = es.Value;
                for (int j = i + 1; j < pts.Count; j++)
                {
                    var (tj, ej, isExit) = pts[j];
                    if (tj > ts + window + 1e-6) break;
                    if (ej == null || ej.Value - prev > maxRise) { ok = false; break; }
                    prev = ej.Value; last = ej.Value;
                    if (isExit) { reached = true; break; }
                }
                if (ok && (reached || es.Value - last >= minDrop)) start = ts;
            }
            double? delay = start - c.T0.Value;
            return (ByTime(delay, c.Th("evac_delay", "good_max_s"), c.Th("evac_delay", "normal_max_s")),
                    new JObject { ["delay_s"] = Round(delay, 1) });
        }

        private static bool IsExitZone(ZoneGraph g, string zoneId) =>
            zoneId != null && g.Zones.Any(z => z.Id == zoneId && z.Type == ZoneType.Exit);

        private static (Grade, JObject) NoElevator(Context c)
        {
            int calls = c.Events.Count(e => e.Type == EventTypes.ElevatorCall);
            var grade = calls <= c.Th("no_elevator", "good_max_count") ? Grade.Good : Grade.Poor;
            return (grade, new JObject { ["calls"] = calls });
        }

        private static (Grade, JObject) DoorCheck(Context c)
        {
            var opens = c.After.Where(e => e.Type == EventTypes.DoorOpen).ToList();
            if (opens.Count == 0) return (Grade.Na, new JObject());
            int checkedCount = opens.Count(e => Bool(e, "checked_before"));
            int hot = opens.Count(e => Bool(e, "hot"));
            var evidence = new JObject { ["checked"] = checkedCount, ["opened"] = opens.Count, ["hot_opened"] = hot };
            if (hot > 0) return (Grade.Poor, evidence);
            double ratio = (double)checkedCount / opens.Count;
            return (ByRatioMin(ratio, c.Th("door_check", "good_min_ratio"), c.Th("door_check", "normal_min_ratio")), evidence);
        }

        private static (Grade, JObject) NoseMouth(Context c)
        {
            double dMin = c.Th("nose_mouth", "smoke_d_min");
            var first = c.Samples.FirstOrDefault(s => s.ZoneD >= dMin);
            if (first == null) return (Grade.Na, new JObject());
            var before = c.Events.Where(e => e.Seq < first.Seq).ToList();
            var lastEquip = before.LastOrDefault(e => e.Type == EventTypes.TowelEquip);
            bool on = lastEquip != null && Bool(lastEquip, "on");
            bool wet = before.Any(e => e.Type == EventTypes.TowelWet);
            var grade = on && wet ? Grade.Good : on ? Grade.Normal : Grade.Poor;
            string towel = on && wet ? "wet" : on ? "dry" : "none";
            return (grade, new JObject { ["towel"] = towel });
        }

        private static (Grade, JObject) LowPosture(Context c)
        {
            var smoky = c.Samples.Where(s => s.LayerY < c.Cfg.StandingHeadM).ToList();
            if (smoky.Count == 0) return (Grade.Na, new JObject());
            double ratio = (double)smoky.Count(s => s.HeadRatio <= c.Cfg.PostureThreshold) / smoky.Count;
            return (ByRatioMin(ratio, c.Th("low_posture", "good_min_ratio"), c.Th("low_posture", "normal_min_ratio")),
                    new JObject { ["low_ratio"] = Math.Round(ratio, 2), ["samples"] = smoky.Count });
        }

        private static (Grade, JObject) Belongings(Context c)
        {
            var picks = c.After.Where(e => e.Type == EventTypes.Pickup && !Bool(e, "essential")).ToList();
            int n = picks.Count;
            var grade = n <= c.Th("belongings", "good_max_count") ? Grade.Good
                      : n <= c.Th("belongings", "normal_max_count") ? Grade.Normal : Grade.Poor;
            return (grade, new JObject { ["count"] = n, ["items"] = new JArray(picks.Select(e => Str(e, "item"))) });
        }

        private static (Grade, JObject) WarnOthers(Context c)
        {
            var npcs = (c.Scenario.Npcs ?? new List<Scenario.NpcSetup>()).Select(n => n.Id).ToList();
            if (npcs.Count == 0) return (Grade.Na, new JObject());
            var helped = new HashSet<string>(c.Events
                .Where(e => e.Type == EventTypes.NpcInteract && (Str(e, "action") == "warn" || Str(e, "action") == "escort"))
                .Select(e => Str(e, "npc")));
            int count = npcs.Count(helped.Contains);
            double ratio = (double)count / npcs.Count;
            var grade = ratio >= c.Th("warn_others", "good_min_ratio") ? Grade.Good : ratio > 0 ? Grade.Normal : Grade.Poor;
            return (grade, new JObject { ["helped"] = count, ["total"] = npcs.Count });
        }

        // ── run-level fields ───────────────────────────────────────────────────

        /// <summary>From the last RUN_END; a log without one (crash, cut upload) counts as aborted.</summary>
        private static Outcome OutcomeOf(IReadOnlyList<LogEvent> events)
        {
            var end = events.LastOrDefault(e => e.Type == EventTypes.RunEnd);
            var s = end == null ? null : Str(end, "outcome");
            switch (s)
            {
                case "escaped": return Outcome.Escaped;
                case "rescued": return Outcome.Rescued;
                case "failed": return Outcome.Failed;
                case "timeout": return Outcome.Timeout;
                default: return Outcome.Aborted;
            }
        }

        private static float ElapsedOf(IReadOnlyList<LogEvent> events)
        {
            var end = events.LastOrDefault(e => e.Type == EventTypes.RunEnd);
            return end?.T ?? (events.Count > 0 ? events[events.Count - 1].T : 0f);
        }

        /// <summary>
        /// Smoke zones where the trainee stayed longest (7장): count SAMPLEs after t0 whose zone_d ≥
        /// nose_mouth.smoke_d_min, per zone; top 2 by count, ties by zone id.
        /// </summary>
        private static List<string> WeakZones(Context c)
        {
            double dMin = c.Th("nose_mouth", "smoke_d_min");
            return c.Samples
                .Where(s => s.Zone != null && s.ZoneD >= dMin)
                .GroupBy(s => s.Zone)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(2).Select(g => g.Key).ToList();
        }

        // ── offline report (16.12) ─────────────────────────────────────────────

        /// <summary>Korean outcome name used by the summary template and the report screen.</summary>
        public static string OutcomeName(Outcome outcome) => OutcomeKo[outcome];

        private static readonly Dictionary<Outcome, string> OutcomeKo = new Dictionary<Outcome, string>
        {
            [Outcome.Escaped] = "탈출 성공",
            [Outcome.Rescued] = "구조됨",
            [Outcome.Failed] = "실패",
            [Outcome.Timeout] = "시간 초과",
            [Outcome.Aborted] = "중단",
        };

        private static readonly Dictionary<string, string> TowelKo = new Dictionary<string, string>
        {
            ["wet"] = "젖은 수건", ["dry"] = "마른 수건", ["none"] = "없음",
        };

        /// <summary>
        /// Offline report from evaluation.json text templates (명세서 16.12), used when the LLM is off.
        /// Per non-na item: good = text.good if graded good else "", improve = text.improve if normal/poor
        /// else "", guideline always. {key} placeholders come from the item's evidence.
        /// Summary: best = highest grade (ties: larger weight, then evaluation.json order), worst = lowest.
        /// </summary>
        public static ReportText TemplateReport(EvaluationResult result, EvaluationConfig cfg)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var items = cfg.Items.ToDictionary(i => i.Id);
            var report = new ReportText();

            var graded = new List<(ItemResult r, EvaluationConfig.Item cfg, int order)>();
            for (int i = 0; i < result.Items.Count; i++)
            {
                var r = result.Items[i];
                if (r.Grade == Grade.Na || !items.TryGetValue(r.Id, out var it)) continue;
                graded.Add((r, it, i));
                report.Items.Add(new ReportItemText
                {
                    Id = r.Id,
                    Good = r.Grade == Grade.Good ? Fill(it.Text?.Good, r.Evidence) : "",
                    Improve = r.Grade != Grade.Good ? Fill(it.Text?.Improve, r.Evidence) : "",
                    Guideline = it.Text?.Guideline ?? "",
                });
            }

            int Rank(Grade g) => g == Grade.Good ? 2 : g == Grade.Normal ? 1 : 0;
            var best = graded.OrderByDescending(x => Rank(x.r.Grade)).ThenByDescending(x => x.cfg.Weight).ThenBy(x => x.order)
                             .FirstOrDefault(x => x.r.Grade != Grade.Poor);
            var worst = graded.OrderBy(x => Rank(x.r.Grade)).ThenByDescending(x => x.cfg.Weight).ThenBy(x => x.order)
                              .FirstOrDefault(x => x.r.Grade != Grade.Good);

            var summaryValues = new Dictionary<string, string>
            {
                ["outcome_ko"] = OutcomeKo[result.Outcome],
                ["outcome"] = OutcomeKo[result.Outcome],
                ["total_pct"] = result.TotalPct.ToString(CultureInfo.InvariantCulture),
                ["best"] = best.cfg?.Name ?? "없음",
                ["worst"] = worst.cfg?.Name ?? "없음",
            };
            report.Summary = Placeholder.Replace(cfg.SummaryTemplate ?? "", m =>
                summaryValues.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
            return report;
        }

        private static readonly Regex Placeholder = new Regex(@"\{([a-z_]+)\}");

        private static string Fill(string template, JObject evidence)
        {
            if (string.IsNullOrEmpty(template)) return "";
            return Placeholder.Replace(template, m =>
            {
                string key = m.Groups[1].Value;
                var v = evidence?[key];
                return v == null ? m.Value : Format(key, v);
            });
        }

        private static string Format(string key, JToken v)
        {
            switch (v.Type)
            {
                case JTokenType.Null: return "-";
                case JTokenType.Array: return v.Any() ? string.Join(", ", v.Select(x => x.ToString())) : "-";
                case JTokenType.Float:
                case JTokenType.Integer:
                    double d = (double)v;
                    if (key.EndsWith("_ratio")) return Math.Round(d * 100).ToString(CultureInfo.InvariantCulture) + "%";
                    return d.ToString("0.#", CultureInfo.InvariantCulture);
                case JTokenType.String:
                    var s = (string)v;
                    return key == "towel" && TowelKo.TryGetValue(s, out var ko) ? ko : s;
                default: return v.ToString();
            }
        }
    }
}
