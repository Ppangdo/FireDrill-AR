# contracts — 모듈 계약 (명세서 14장의 코드판)

| 위치 | 내용 |
| --- | --- |
| `schemas/*.schema.json` | JSON Schema (draft 2020-12). 설정 파일 7종, 로그 이벤트, 평가 결과, 표준 로그 기대값, 플레이 스크립트 |
| `../unity/Assets/FireDrill/Config/*.json` | 데모 설정 파일 7종 (`demo_3f`, `s_demo_fire`) |
| `../unity/Assets/FireDrill/Core/Contracts/` | C# DTO·인터페이스 (`FireDrill.Core`, UnityEngine 없음) |
| `../unity/Assets/FireDrill/Runtime/Contracts/` | 플랫폼 어댑터 인터페이스 (`IPlayerRig`, `IInteractionSource`, `ISpaceProvider`) |
| `../server/app/schemas.py` | 서버 API Pydantic 모델 |
| `golden/` | 표준 로그 3종 + 기대 평가, 화재 참조 궤적 `fire_demo.trace.json` (모두 `gen_golden.py`로 생성) |
| `playscripts/` | 자동 플레이 스크립트 (15.2) |
| `tools/gen_golden.py` | 표준 로그 생성기 (stdlib) |
| `tools/ref_eval.py` | 참조 평가기. C# Evaluator는 이것과 같은 등급을 내야 한다 |
| `tools/ref_fire.py` | 참조 화재 모델. C# FireEngine은 이것과 D가 0.01 안에서 같아야 하고, `fire.json`은 `scenario.json`의 `tuning_targets`를 만족해야 한다 |

## 검증

```
uv run --project server python -m pytest server/tests        # 스키마·상호 참조·표준 로그·화재 궤적 + 서버 엔드포인트 (91개)
Unity -batchmode -nographics -projectPath unity -runTests -testPlatform EditMode -testResults ../out/editmode.xml   # 84개
```

`fire.json`·`scenario.json`·`zone_graph.json`을 바꾸면 `gen_golden.py`를 다시 돌려 `fire_demo.trace.json`을 갱신한다 (CI가 차이를 잡는다). C# 테스트 `FireEngineTests.MatchesReferenceModel_180s`가 이 파일과 비교한다.

## 1단계(Core) 구현 메모

명세서·`measure` 문장에 정해져 있지 않아 구현에서 고른 것. 바꾸려면 `Core/` 코드와 테스트를 함께 고친다.

- **구현 위치**: `ZoneGraph`(`Core/Spatial/`), `FireEngine`·`Exposure`(`Core/Fire/`), `EventLog`(`Core/Log/`), `Evaluator`(`Core/Evaluation/`). 공개 API는 0단계 계약 그대로이고, 테스트용 내부 멤버(`Area`, `IsBlocked`, `ExitDistance(zone, pos)` 등)만 `InternalsVisibleTo`로 연다.
- **`ZoneGraph.Load` 오류**: `$.zones[2].ceiling_h: required field is missing`처럼 JSON 경로로 시작한다. 스키마 검사(필수·형식·패턴·enum·모르는 필드) 다음에 참조 검사(중복 id, 없는 구역, 출구 없음).
- **`ZoneAt`**: 두 구역이 맞닿은 벽 위의 점은 `zones` 목록에서 먼저 나오는 구역. 평가기는 로그에 적힌 `zone`으로 출구 거리를 잰다(`ref_eval.py`와 같음).
- **`PathTo`/`PathToExit`**: 문 중심을 잇는 다익스트라. 마지막 경유점은 도착 구역의 `spawn`, 없으면 다각형 무게중심. 높이(y)는 출발점 y. 출구 구역은 경유하지 않는다.
- **소화**: 초기 단계에서 사거리 안, 누적 분사 `extinguish_needed_s` 이상이면 진화. 진화 뒤 발화 구역은 연기를 더 내지 않고 +200°C도 사라진다. 이미 퍼진 연기는 남는다(환기 없음).
- **FED**: 1.0에서 멈춘다. 설정값이 float이라 임계값과 1e-6 안이면 도달로 본다(c=1에서 정확히 600틱에 실패).
- **평가 결과**: 해당 없음 항목도 `max`에 배점을 적고 `points` 0 (총점에서는 빠짐). `timeline`은 모든 SAMPLE. `weak_zones`는 t0 이후 `zone_d ≥ nose_mouth.smoke_d_min`인 SAMPLE 수가 많은 구역 상위 2개(같으면 id 순). RUN_END가 없으면 `aborted`, `elapsed_s`는 RUN_END(없으면 마지막 이벤트)의 t.
- **문장 틀(`TemplateReport`)**: 해당 없음 항목은 뺀다. 좋음이면 `good`만, 보통·미흡이면 `improve`만 채우고(다른 쪽은 빈 문자열), `guideline`은 항상. 요약의 "가장 잘한 점"은 등급이 가장 높은 항목(같으면 배점 큰 것, 그다음 evaluation.json 순서), "가장 먼저 고칠 점"은 가장 낮은 항목. 모두 좋음이면 고칠 점은 "없음". `{low_ratio}`는 백분율, `{towel}`은 "젖은 수건/마른 수건/없음", 값이 없으면 "-".

## 2단계(서버) 구현 메모

명세서 8·9·14.6장에 정해져 있지 않아 구현에서 고른 것. 코드는 `server/app/`, 테스트는 `server/tests/test_server.py`.

- **구조**: `settings.py`(환경 변수·수치), `db.py`(9장 테이블), `factory.py`(`create_app`, 엔드포인트), `llm.py`(중계·대체 응답), `templates.py`(문장 틀·NPC 예비 대사), `devices.py`(WebSocket 기기 상태·중단), `static/staff.html`(요원 화면), `main.py`(uvicorn 진입점).
- **14.6이 9장보다 우선**: `/health`는 `{ok, llm: "claude"|"fake"|"off", server_time}`, `/runs/{id}/finish`는 `{ok: true}`, `/runs/{id}/report`는 요청 본문에 `evaluation`을 받는다.
- **토큰**: `DEVICE_TOKENS`에 있는 토큰이면 어느 기기든 REST를 쓸 수 있다. WebSocket은 토큰이 경로의 `device_id`와 짝이어야 한다(아니면 1008로 닫음). 폰 브라우저용으로 `X-Device-Token` 대신 `?token=`도 받는다.
- **LLM 대체는 HTTP 200**: 타임아웃·API 오류·거절·스키마 불일치면 NPC는 `source: "fallback"`, 리포트는 `fallback_used: true, model: "template"`로 200을 준다. `llm_timeout`(504)·`llm_error`(502) 코드는 정의만 있고 지금은 쓰지 않는다. `LLM_MODE=claude`인데 키가 없으면 `off`로 동작한다(`/health`도 `off`).
- **리포트 항목 id 집합**: 입력에서 `na`가 아닌 항목(문장 틀과 같은 집합). 하나라도 빠지거나 더 있거나 겹치면 문장 틀로 대체한다. 순서는 입력 순서로 맞춘다.
- **NPC 예비 대사**: `npc_cache.json`의 `fallback[npc_id]` 중 `smoke_level % 개수`번째, 행동은 `current_action` 유지, 감정 `scared`. 모르는 NPC는 "…".
- **`fake` 모드**: NPC는 warn이면 evacuate, escort면 follow_player, 그 밖에는 현재 행동 유지. 리포트는 문장 틀을 LLM 응답처럼 돌려준다(`fallback_used: false, model: "fake"`). `llm_calls`에도 남긴다.
- **finish 검사**: `evaluation.run_id`가 경로와 같고 `outcome`이 `evaluation.outcome`과 같아야 한다(아니면 422). 다시 보내면 덮어쓴다. `evaluations`에는 9장 컬럼에 `outcome`·`elapsed_s`·`timeline`을 더 저장한다.
- **이벤트**: 묶음 안의 `run_id`가 경로와 다르면 422. `(run_id, seq)`가 이미 있거나 같은 묶음에서 반복되면 건너뛴다. `last_seq`는 그 run에 저장된 가장 큰 seq(없으면 0). `events`에는 `zone_d`·`layer_y`도 저장한다.
- **CSV**: UTF-8 BOM(엑셀 한글), 열은 `run_id, seq, t, type, zone, x, y, z, head_ratio, c_head, fed, zone_d, layer_y, data(JSON)`.
- **WebSocket 서버**: 이 PC에서 `websockets`는 설치가 막혀 순수 Python인 `wsproto`를 쓴다(`--ws wsproto`). 잘못된 메시지는 무시하고 연결은 유지한다. 재접속하면 새 소켓이 이전 것을 대신한다.
- **Claude 호출**: NPC `claude-haiku-5-5` effort low, 리포트 `claude-sonnet-5-5` effort medium + 서버 측 fallback(`fallbacks: "default"`, 베타 `server-side-fallback-2026-07-01`). 둘 다 구조화 출력(`output_config.format`), 시스템 프롬프트 고정 + `cache_control`, SDK 재시도 0회(마감은 서버가 지킨다). `llm_calls.input_tokens`는 캐시 읽기·쓰기 토큰을 포함한다.

## 명세서 반영 대기

1. **14.6 요원 화면 보조 엔드포인트**: `GET /staff/devices` → `{schema_version, devices: [{device_id, connected, status: WsStatus|null, status_age_s}]}`. 요원 화면이 1초마다 읽는다. 9·14.6 표에 추가하면 된다.
2. **14.6 토큰**: `?token=` 쿼리도 받는다는 것과 WebSocket 토큰·기기 짝 규칙(위 메모).
3. **14.6 오류**: LLM 실패는 504/502가 아니라 200 + 대체 응답이라는 것(위 메모).
4. **15.3 서버 명령**: 이 PC에서는 `uv run --project server python -m uvicorn app.main:app --app-dir server --host 0.0.0.0 --port 8000 --ws wsproto`, 테스트는 `uv run --project server python -m pytest server/tests`.

## 명세서 반영 완료 (2026-11-11)

0단계에서 계약을 코드로 옮기며 명세서와 달라진 점. 명세서 1·4·6·7·8·11·12·14·15장과 새 17장("나중에 바꿀 수 있는 것")에 반영했다. 같은 날 추가된 결정: 화재 튜닝 목표와 계수(`source_rate` 0.04, 문 0.3, 통로 0.02, `e_corr_sm` 0.01), 닫힌 창고 문이 발화 38초 뒤 열리는 `door_events`, 튜토리얼 종료 조건 `tutorial`, `app_config.skip_calibration`.

1. **로그 공통 필드 추가**: `zone_d`(현재 구역 연기 농도), `layer_y`(연기층 높이). 평가 항목 `nose_mouth`·`low_posture`가 로그만으로 판정하려면 필요하다. `Snapshot`에도 추가.
2. **이벤트 data 추가**: `TOWEL_EQUIP.on`(착용/해제 토글), `ALARM_PRESSED.object`.
3. **`belongings` 측정 변경**: 시간(초) → t0 이후 비필수 아이템 줍기 **개수** (0 좋음, 1 보통, 2 이상 미흡). 1Hz 표본으로는 "머문 시간"을 일관되게 잴 수 없어서.
4. **`evac_delay` 알고리즘 확정**: 연속 출구 거리 표본에서 첫 걸음이 0.1m 이상 줄고, 5초 창 안에서 0.05m 넘게 늘지 않으며, 1.5m 이상 줄거나 출구에 도착한 첫 표본. 출구 진입은 거리 0 표본으로 취급 (evaluation.json `measure`).
5. **온도 계수 조정**: `per_d_c` 180 → 60, `origin_bonus_c` 100 → 200. 기존 값이면 복도 연기 D 0.22만 돼도 방문이 "뜨거움"이 되어, 연기만 찬 복도로 나가는 정상 행동이 미흡이 됐다. 이제 화점 근처(D ≥ 0.67 또는 발화 구역)만 뜨겁다.
6. **NPC 선택지 `warn`은 `SHOUT(method=choice)`도 남긴다** (화재 알리기 판정과 연결).
7. **`NpcReactRequest` 평탄화**: `npc_id, persona, current_action, zone, player_action, smoke_level, recent_events`.
8. **표준 로그 기대값**: `na_run`은 계단 문을 확인하고 여는 플레이라 `door_check`가 좋음, 해당 없음은 `nose_mouth`·`low_posture`. `fail_run`은 가방 1개만 줍고 엘리베이터 앞에서 기다리다 쓰러진다 (`belongings` 보통, 총점 6%).
9. **스키마 필드 보강**: 간선 `center`·`wall_axis` 필수, `side_from_handle` null 허용(양쪽 손잡이). 시나리오 `retry_spawn`·`retry_fire_at_s`·NPC `home_zone`. evaluation.json `standing_head_m`·`summary_template`·항목 `name`·`measure`.
10. **`ZoneGraph.ExitDistance`는 `float?`** (모든 구역 밖이면 null). `FlowState` enum 추가.
11. **개발 환경**: 이 PC는 Windows 스마트 앱 컨트롤이 서명 없는 실행 파일을 막는다. 그래서 uv가 받는 Python 대신 시스템 Python 3.15를 쓰고, pytest는 `python -m pytest`로 실행한다. `uvicorn[standard]` 대신 `uvicorn`(pyyaml 빌드가 막힘).

2단계 시작 때 명세서 4·11·15장에 반영한 1단계 항목:

1. **11장 완료 기준 — 화재 엔진**: "튜닝 목표 곡선(90초 0.5, 150초 0.8)을 ±0.1 안에서 재현"은 4장의 확정 튜닝 목표(`tuning_targets`)와 맞지 않는다(지금 수치로 그 곡선을 따르는 구역이 없다). 1단계 테스트는 대신 ① 참조 모델과 D 0.01 이내(실제 최대 오차 5e-7), ② `tuning_targets`, ③ 닫힌 문 뒤 방(301·302호) 150초에 D < 0.2, ④ 같은 입력 같은 결과를 확인한다. 11장 문장을 이렇게 바꿨다.
2. **4장 튜닝 목표 표 "현재 수치로 결과"**: 1초 간격으로 재면 중간 복도 71초, 남쪽 복도 158초다(표에는 70·157초). 둘 다 허용 범위 안.
3. **CLAUDE.md·15.3의 EditMode 명령**: `-testResults`는 `unity/` 기준 상대 경로라서 `out/editmode.xml`이면 `unity/out/`에 생긴다. `../out/editmode.xml`로 고쳤다.
