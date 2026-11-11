# contracts — 모듈 계약 (명세서 14장의 코드판)

| 위치 | 내용 |
| --- | --- |
| `schemas/*.schema.json` | JSON Schema (draft 2020-12). 설정 파일 7종, 로그 이벤트, 평가 결과, 표준 로그 기대값, 플레이 스크립트 |
| `../unity/Assets/FireDrill/Config/*.json` | 데모 설정 파일 7종 (`demo_3f`, `s_demo_fire`) |
| `../unity/Assets/FireDrill/Core/Contracts/` | C# DTO·인터페이스 (`FireDrill.Core`, UnityEngine 없음) |
| `../unity/Assets/FireDrill/Runtime/Contracts/` | 플랫폼 어댑터 인터페이스 (`IPlayerRig`, `IInteractionSource`, `ISpaceProvider`) |
| `../server/app/schemas.py` | 서버 API Pydantic 모델 |
| `golden/` | 표준 로그 3종 + 기대 평가 (`gen_golden.py`로 생성) |
| `playscripts/` | 자동 플레이 스크립트 (15.2) |
| `tools/gen_golden.py` | 표준 로그 생성기 (stdlib) |
| `tools/ref_eval.py` | 참조 평가기. C# Evaluator는 이것과 같은 등급을 내야 한다 |

## 검증 (0단계 통과 조건)

```
uv run --project server python -m pytest server/tests        # 스키마·상호 참조·표준 로그 기대값 (24개)
Unity -batchmode -nographics -projectPath unity -runTests -testPlatform EditMode -testResults out/editmode.xml
```

| `tools/ref_fire.py` | 참조 화재 모델. C# FireEngine은 이것과 D가 0.01 안에서 같아야 하고, `fire.json`은 `scenario.json`의 `tuning_targets`를 만족해야 한다 |

## 명세서 반영 대기

(없음)

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
