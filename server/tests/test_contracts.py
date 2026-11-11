"""Stage 0 gate (명세서 15장): every contract file validates, references resolve,
and the golden expectations follow from the evaluation rules."""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[2]
SCHEMAS = ROOT / "contracts" / "schemas"
CONFIG = ROOT / "unity" / "Assets" / "FireDrill" / "Config"
GOLDEN = ROOT / "contracts" / "golden"
PLAYSCRIPTS = ROOT / "contracts" / "playscripts"

sys.path.insert(0, str(ROOT / "contracts" / "tools"))
sys.path.insert(0, str(ROOT / "server"))

import ref_eval  # noqa: E402
from app import schemas as api  # noqa: E402


def load(p: Path):
    return json.loads(p.read_text(encoding="utf-8"))


def validator(name: str) -> Draft202012Validator:
    schema = load(SCHEMAS / f"{name}.schema.json")
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def assert_valid(v: Draft202012Validator, doc, label: str) -> None:
    errors = sorted(v.iter_errors(doc), key=lambda e: list(e.absolute_path))
    assert not errors, label + ":\n" + "\n".join(f"  {list(e.absolute_path)}: {e.message}" for e in errors)


CONFIG_FILES = ["zone_graph", "scenario", "fire", "evaluation", "dialogue", "npc_cache", "app_config"]
GOLDEN_NAMES = ["model_run", "fail_run", "na_run"]


@pytest.mark.parametrize("name", CONFIG_FILES)
def test_config_matches_schema(name):
    assert_valid(validator(name), load(CONFIG / f"{name}.json"), f"{name}.json")


def test_all_schemas_are_valid_json_schema():
    for p in SCHEMAS.glob("*.schema.json"):
        Draft202012Validator.check_schema(load(p))


# ---- cross references ------------------------------------------------------

@pytest.fixture(scope="module")
def zg():
    return load(CONFIG / "zone_graph.json")


@pytest.fixture(scope="module")
def scenario():
    return load(CONFIG / "scenario.json")


def test_zone_graph_references(zg):
    zone_ids = [z["id"] for z in zg["zones"]]
    assert len(zone_ids) == len(set(zone_ids)), "duplicate zone id"
    edge_ids = [e["id"] for e in zg["edges"]]
    assert len(edge_ids) == len(set(edge_ids)), "duplicate edge id"
    obj_ids = [o["id"] for z in zg["zones"] for o in z["objects"]]
    assert len(obj_ids) == len(set(obj_ids)), "duplicate object id"
    for e in zg["edges"]:
        assert e["from"] in zone_ids and e["to"] in zone_ids, e["id"]
        if e["type"] == "door":
            assert "door_open" in e and e.get("handle") is not None, f"{e['id']} door needs door_open and handle"
    assert any(z["type"] == "exit" for z in zg["zones"]), "no exit zone"


def test_every_zone_reaches_an_exit(zg):
    g = ref_eval.ZoneGraph(zg)
    for z in zg["zones"]:
        cx = sum(p[0] for p in z["polygon"]) / len(z["polygon"])
        cz = sum(p[1] for p in z["polygon"]) / len(z["polygon"])
        d = g.exit_distance(z["id"], [cx, 0, cz])
        assert d is not None and d < float("inf"), f"{z['id']} cannot reach an exit"


def test_scenario_references(zg, scenario):
    zone_ids = {z["id"] for z in zg["zones"]}
    edge_ids = {e["id"] for e in zg["edges"]}
    assert scenario["building_id"] == zg["building_id"]
    assert scenario["fire"]["origin_zone"] in zone_ids
    assert set(scenario["initial_doors"]) <= edge_ids
    assert set(scenario["blocked_edges"]) <= edge_ids
    for n in scenario["npcs"]:
        assert n.get("home_zone") in zone_ids


def test_dialogue_references():
    d = load(CONFIG / "dialogue.json")
    assert d["start"] in d["nodes"]
    for nid, node in d["nodes"].items():
        if node.get("next"):
            assert node["next"] in d["nodes"], f"{nid}.next"
        for c in node.get("choices", []):
            if c.get("next"):
                assert c["next"] in d["nodes"], f"{nid} choice next"


def test_npc_cache_references(scenario):
    cache = load(CONFIG / "npc_cache.json")
    npc_ids = {n["id"] for n in scenario["npcs"]}
    assert set(cache["fallback"]) == npc_ids
    for key in cache["entries"]:
        assert key.split("|")[0] in npc_ids, key


def test_evaluation_rules_cover_items():
    ev = load(CONFIG / "evaluation.json")
    ids = [i["id"] for i in ev["items"]]
    assert len(ids) == len(set(ids)) == 10
    for i in ev["items"]:
        assert i["rule_ref"] in ev["rules"], i["id"]


def test_app_config_matches_scenario(scenario, zg):
    app = load(CONFIG / "app_config.json")
    assert app["scenario_id"] == scenario["id"]
    assert app["building_id"] == zg["building_id"]


# ---- golden logs -----------------------------------------------------------

@pytest.mark.parametrize("name", GOLDEN_NAMES)
def test_golden_log_lines(name, zg):
    v = validator("log_event")
    zone_ids = {z["id"] for z in zg["zones"]}
    events = ref_eval.load_log(GOLDEN / f"{name}.jsonl")
    assert events, "empty log"
    for i, e in enumerate(events, start=1):
        assert_valid(v, e, f"{name} line {i}")
        api.LogEvent.model_validate(e)
        assert e["seq"] == i, f"{name} seq gap at line {i}"
        assert e["zone"] is None or e["zone"] in zone_ids
    ts = [e["t"] for e in events]
    assert ts == sorted(ts), f"{name} time goes backwards"
    assert events[0]["type"] == "RUN_START" and events[-1]["type"] == "RUN_END"


@pytest.mark.parametrize("name", GOLDEN_NAMES)
def test_golden_expected(name):
    exp = load(GOLDEN / f"{name}.expected.json")
    assert_valid(validator("golden_expected"), exp, f"{name}.expected.json")
    g, s, c = ref_eval.default_inputs()
    got = ref_eval.evaluate(ref_eval.load_log(GOLDEN / exp["log"]), g, s, c)
    assert got["outcome"] == exp["outcome"]
    assert got["grades"] == exp["grades"]
    assert got["total_pct"] == exp["total_pct"]


# ---- play scripts ----------------------------------------------------------

@pytest.mark.parametrize("path", sorted(PLAYSCRIPTS.glob("*.json")), ids=lambda p: p.name)
def test_playscripts(path, zg):
    doc = load(path)
    assert_valid(validator("playscript"), doc, path.name)
    obj_ids = {o["id"] for z in zg["zones"] for o in z["objects"]}
    edge_ids = {e["id"] for e in zg["edges"]}
    npc_ids = {n["id"] for n in load(CONFIG / "scenario.json")["npcs"]}
    for step in doc["steps"]:
        if "target" in step:
            assert step["target"] in obj_ids | edge_ids | npc_ids, f"{path.name}: unknown target {step['target']}"


# ---- pydantic models round-trip --------------------------------------------

def test_api_models_examples():
    api.NpcReactRequest.model_validate({
        "npc_id": "npc_minji", "persona": "p", "current_action": "freeze", "zone": "z_lab_302",
        "player_action": "warn", "smoke_level": 2, "recent_events": ["SHOUT"]})
    api.NpcReactResponse.model_validate({"action": "follow_player", "line": "따라갈게요", "emotion": "scared", "source": "llm"})
    api.WsStatus.model_validate({"run_id": None, "state": "Boot", "t": 0, "zone": None, "fed": 0, "stage": 0})
    with pytest.raises(Exception):
        api.NpcReaction.model_validate({"action": "fly", "line": "x", "emotion": "calm"})
