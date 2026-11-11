"""Reference fire model in Python (stdlib only) — 명세서 4장.

The C# FireEngine (Stage 1) must produce the same zone densities within 0.01.
Used by server/tests to check that fire.json + scenario.json meet the scenario's
tuning_targets, so tuning values can change without breaking the demo's intent.

Rules (one tick, dt = fire.tick_s):
  1. Origin zone (after ignition, while not extinguished): D += source_rate * dt, capped at 1.
  2. Door events (scenario.door_events, at_s after ignition) change edge state before flows.
  3. Every flow is computed from the D values at the start of the tick (simultaneous update):
       k = fire.edge_k[edge] if present, else k[door_open|door_closed|opening|stair]
       flow = k * width * (D_hi - D_lo) * dt
       D_lo += flow * area_ref / A_lo ;  D_hi -= flow * area_ref / A_hi
  4. Exit zones vent to the outside: their D stays 0. All D clamped to [0, 1].
  layer_y = ceiling_h - D * (ceiling_h - min_layer_y)
"""
from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / "unity" / "Assets" / "FireDrill" / "Config"


def polygon_area(poly) -> float:
    s = 0.0
    for i in range(len(poly)):
        x1, z1 = poly[i]
        x2, z2 = poly[(i + 1) % len(poly)]
        s += x1 * z2 - x2 * z1
    return abs(s) / 2


class FireModel:
    def __init__(self, zone_graph: dict, fire: dict, scenario: dict):
        self.fire = fire
        self.scenario = scenario
        self.zones = {z["id"]: z for z in zone_graph["zones"]}
        self.area = {zid: polygon_area(z["polygon"]) for zid, z in self.zones.items()}
        self.exits = {zid for zid, z in self.zones.items() if z["type"] == "exit"}
        blocked = set(scenario.get("blocked_edges", []))
        self.edges = [e for e in zone_graph["edges"] if e["id"] not in blocked]
        self.door_open = {e["id"]: e.get("door_open", True) for e in self.edges if e["type"] == "door"}
        self.door_open.update(scenario.get("initial_doors", {}))
        self.events = sorted(scenario.get("door_events", []), key=lambda ev: ev["at_s"])
        self.D = {zid: 0.0 for zid in self.zones}
        self.t_fire = 0.0  # seconds since ignition

    def k_of(self, e: dict) -> float:
        over = self.fire.get("edge_k", {})
        if e["id"] in over:
            return over[e["id"]]
        k = self.fire["k"]
        if e["type"] == "door":
            return k["door_open"] if self.door_open[e["id"]] else k["door_closed"]
        return k[e["type"]]

    def tick(self) -> None:
        dt = self.fire["tick_s"]
        while self.events and self.events[0]["at_s"] <= self.t_fire + 1e-9:
            ev = self.events.pop(0)
            if ev["action"] in ("burst_open", "open"):
                self.door_open[ev["edge"]] = True
            elif ev["action"] == "close":
                self.door_open[ev["edge"]] = False
        origin = self.scenario["fire"]["origin_zone"]
        start = dict(self.D)
        start[origin] = min(1.0, start[origin] + self.fire["source_rate"] * dt)
        new = dict(start)
        aref = self.fire["area_ref_m2"]
        for e in self.edges:
            a, b = e["from"], e["to"]
            hi, lo = (a, b) if start[a] >= start[b] else (b, a)
            flow = self.k_of(e) * e["width"] * (start[hi] - start[lo]) * dt
            new[lo] += flow * aref / self.area[lo]
            new[hi] -= flow * aref / self.area[hi]
        for zid in new:
            new[zid] = 0.0 if zid in self.exits else max(0.0, min(1.0, new[zid]))
        self.D = new
        self.t_fire = round(self.t_fire + dt, 6)

    def layer_y(self, zid: str) -> float:
        ceiling = self.zones[zid]["ceiling_h"]
        return ceiling - self.D[zid] * (ceiling - self.fire["min_layer_y"])

    def run(self, seconds: float, every_s: float = 1.0):
        """Yield (t_fire, {zone: D}) every `every_s` seconds, starting at t=0."""
        step = round(every_s / self.fire["tick_s"])
        n = round(seconds / self.fire["tick_s"])
        for i in range(n + 1):
            if i % step == 0:
                yield self.t_fire, dict(self.D)
            self.tick()


def check_targets(zone_graph: dict, fire: dict, scenario: dict) -> list[str]:
    """Return a list of human-readable failures (empty = all tuning targets met)."""
    targets = scenario.get("tuning_targets", [])
    if not targets:
        return []
    horizon = max(t.get("at_s", 0) + t.get("tolerance_s", 0) for t in targets) + 1
    model = FireModel(zone_graph, fire, scenario)
    first_below: dict[str, float | None] = {t["zone"]: None for t in targets}
    for t_fire, _ in model.run(horizon):
        for t in targets:
            z = t["zone"]
            if first_below[z] is None and model.layer_y(z) < t["height_m"]:
                first_below[z] = t_fire
    failures = []
    for t in targets:
        hit = first_below[t["zone"]]
        if t["kind"] == "reach_below":
            lo, hi = t["at_s"] - t["tolerance_s"], t["at_s"] + t["tolerance_s"]
            if hit is None or not (lo <= hit <= hi):
                failures.append(f"{t['zone']}: layer below {t['height_m']} m at {hit}s, want {lo}-{hi}s")
        elif t["kind"] == "stay_above":
            if hit is not None and hit < t["at_s"]:
                failures.append(f"{t['zone']}: layer below {t['height_m']} m at {hit}s, want after {t['at_s']}s")
    return failures


def load_defaults():
    def load(name):
        return json.loads((CONFIG / f"{name}.json").read_text(encoding="utf-8"))
    return load("zone_graph"), load("fire"), load("scenario")


if __name__ == "__main__":
    zg, fire, sc = load_defaults()
    m = FireModel(zg, fire, sc)
    watch = ["z_storage_303", "z_corr_n", "z_corr_m", "z_elev_lobby", "z_corr_s", "z_lab_302"]
    print("t_fire " + " ".join(w[2:12].ljust(10) for w in watch) + "   (D / layer_y)")
    for t, d in m.run(180, every_s=10):
        print(f"{t:6.0f} " + " ".join(f"{d[w]:.2f}/{m.layer_y(w):.2f}".ljust(10) for w in watch))
    fails = check_targets(zg, fire, sc)
    print("tuning targets:", "OK" if not fails else fails)
