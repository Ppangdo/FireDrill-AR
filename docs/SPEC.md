# AR 화재 대피 훈련 플랫폼 AI 구현 명세서

> 원본: Claude Doc (2026-11-11 작성, 2026-11-12 내보냄) · 팀 우리는한다스
> 이 파일이 명세서의 저장소 사본이다. 앞으로는 이 파일을 고친다.

## 개요

이 문서는 **AI가 코드를 구현할 때 따르는 단일 기준**이다. 과제계획서(10/14, 10/28)의 내용을 모듈별 입력·출력·규칙·수치·완료 기준으로 바꿨다. 계획서에 없던 수치는 **초안 값**이며 모두 설정 파일로 빼서 튜닝한다. 사람이 정해야 할 항목은 마지막 "미결정 사항"에 모았다.

**확정된 결정 (2026-11-11)**: 서버 Python FastAPI · LLM Claude · 입력은 손 추적 + 안전요원 중단 · 12/18까지 PC 완성 + Quest 복도 데모.

**추가 결정 (2026-11-11)**: 훈련 건물 미정(예시 구조로 구현) · 근거는 국민재난안전포털과 소방청 자료 대조 · 튜토리얼은 교수님 심부름 · 실패 연출은 3단계 전부 · 12/18 전 테스트는 팀원끼리 · **Quest 3 미확보** · 서버는 시연 노트북 · API 키는 팀장 관리. 자세한 내용은 12장.

**범위.** 12/18까지는 MVP를 완성하고, 나머지는 확장 단계로 둔다.

| 구분 | MVP (12/18까지) | 확장 단계 |
| --- | --- | --- |
| 플랫폼 | PC 프로토타입에서 전체 흐름 완성 + Quest 3 복도 1곳 패스스루 데모(W12까지 기기를 확보한 경우) | Quest 3에서 복도+방 전체 플레이 |
| 공간 | PC: 실제 층 평면을 본뜬 그레이박스 씬 / Quest: 복도 1곳 | 층 전체, 층간(계단) |
| 시나리오 | 화재 1종, 발화점 1개 고정 | 발화점·차단 경로 변형 3개 이상 |
| 평가 | 규칙 기반 8개 항목 + LLM 리포트 | 10개 항목 |
| NPC | 이름 있는 NPC 2명, 캐시 대사 + 런타임 생성 | 3명 + 군중 |
| 대화 | 선택지 대화 | STT/TTS 음성 대화 |

**용어**

- **구역(zone)**: 방·복도·계단·출구 하나. 연기와 온도를 계산하는 최소 단위.
- **구역 그래프**: 구역을 노드, 문·통로를 간선으로 한 그래프.
- **세션 / 시도(run)**: 세션은 훈련자 1명의 한 번 방문, run은 그 안의 한 번 플레이(재시도마다 새 run).
- **머리 높이 연기 농도**: 훈련자 머리 높이에서 계산한 연기 농도(0\~1).
- **FED**: 유독가스 누적 노출량(Fractional Effective Dose). 1.0에 도달하면 실패.

## 1. AI 작업 규칙과 기술 스택

AI는 아래 규칙을 지킨다. 이 문서와 다르게 구현해야 하면 코드를 쓰기 전에 멈추고 질문한다.

1. **수치는 코드에 직접 쓰지 않는다.** 이 문서의 모든 초안 값은 `config/*.json`(클라이언트)과 `server/settings.py`(서버)에 둔다.
2. **로직과 엔진을 분리한다.** 화재 엔진·평가 엔진·구역 그래프는 `UnityEngine`을 참조하지 않는 순수 C# 어셈블리(`FireDrill.Core`)로 만들어 EditMode 테스트로 검증한다.
3. **PC와 Quest는 같은 코어를 쓴다.** 차이는 입력·카메라·공간 정렬 어댑터(`IPlayerRig`, `ISpaceProvider`)에만 둔다.
4. **평가는 클라이언트에서 한다.** 오프라인 모드에서도 점수가 나오게 하기 위해서다. 서버는 저장과 LLM 중계만 맡는다.
5. **LLM은 판정하지 않는다.** 점수·판정을 LLM 입력으로 주고, LLM 출력으로 점수를 바꾸는 코드는 쓰지 않는다.
6. **모든 외부 호출에는 타임아웃과 대체 경로가 있다.** (8장 참고)
7. **모듈마다 완료 기준(11장)을 테스트로 만든 뒤 구현한다.**

| 영역 | 선택 | 비고 |
| --- | --- | --- |
| 엔진 | Unity 6000.6.8f1, URP | Quest 3 성능 때문에 URP 고정 |
| XR | Meta XR SDK (All-in-One), MR Utility Kit, XR Interaction Toolkit 3.x | 패스스루·공간 앵커·깊이 가림·손 추적 |
| 클라이언트 언어 | C# | 코어는 .NET Standard 2.1 |
| 서버 | Python 3.12 이상(개발 PC는 시스템 Python 3.15), FastAPI, SQLModel, uvicorn | 웹소켓은 FastAPI 기본 기능 |
| DB | SQLite(개발·시연), PostgreSQL(배포 시) | SQLModel로 교체 가능하게 |
| LLM | Anthropic Python SDK | NPC: `claude-haiku-5-5` (effort low) / 리포트: `claude-sonnet-5-5` (effort medium) |
| 테스트 | Unity Test Framework(EditMode), pytest, GitHub Actions(계약 테스트) | 코어는 전부 EditMode로 |
| 협업 | Git/GitHub, Git LFS(에셋) | 브랜치 = 모듈 단위 |

저장소 구조(초안):

```text
firedrill/
  unity/Assets/FireDrill/
    Core/          # 순수 C#: ZoneGraph, FireEngine, EventLog, Evaluator
    Runtime/       # MonoBehaviour: GameFlow, SmokeRenderer, Interactables, NPC
    Platform/PC/   # 키보드·마우스 리그, 그레이박스 공간
    Platform/Quest/ # 패스스루, MRUK, 앵커, 손 추적
    Config/        # fire.json, evaluation.json, npc_cache.json
    Tests/EditMode/
  server/
    app/           # main.py, routers/, models.py, llm.py, settings.py
    prompts/       # npc_system.md, report_system.md
    scripts/       # build_npc_cache.py
    tests/
  docs/            # 이 명세서의 내보내기, 이벤트 정의서
```

## 2. 시스템 구성

```text
클라이언트 (PC · Quest 3 공통)                 서버 (Python FastAPI)
┌──────────────────────────────┐   HTTP 요청   ┌──────────────────────────┐
│ 플랫폼 어댑터                │ ────────────▶ │ REST API · DB (SQLite)   │
│  PC: 키보드·마우스·그레이박스 │               ├──────────────────────────┤
│  Quest: 패스스루·MRUK·손 추적 │  중단 (WS)    │ 요원 화면 · WebSocket     │
├──────────────────────────────┤ ◀──────────── │  /staff → 1초 안에 Aborted│
│ Runtime (Unity)              │               ├──────────────────────────┤
│  GameFlow·연기 렌더·상호작용  │               │ LLM 중계 (NPC 3초,       │
│  NPC·리포트 화면             │               │  리포트 10초, 대체 응답) │
├──────────────────────────────┤               └────────────┬─────────────┘
│ Core (순수 C#, 오프라인 동작) │                            │ Anthropic SDK
│  구역 그래프·화재 엔진·FED    │               ┌────────────▼─────────────┐
│  이벤트 로그·평가 엔진        │               │ Claude API               │
└──────────────────────────────┘               │ Haiku: NPC, Sonnet: 리포트│
  수치: config/*.json · npc_cache.json         └──────────────────────────┘
```

화재 계산과 평가는 클라이언트의 Core에서 끝나므로 서버가 없어도 훈련과 점수는 나온다. 서버가 끊기면 NPC는 캐시·예비 대사로, 리포트는 문장 틀로 대체된다.

## 3. 구역 그래프

구역 그래프는 화재 엔진·평가·NPC가 공유하는 공간 모델이다. 건물마다 JSON 파일 하나(`zone_graph.json`)로 저장한다.

- **좌표계**: 미터, Y축 위. 원점은 보정 기준점 A(층마다 바닥에 표시), +Z는 기준점 A→B 방향.
- **PC**: 그레이박스 씬에 `ZoneVolume`·`ZoneLink` 컴포넌트를 배치하고, 에디터 메뉴로 같은 JSON을 내보낸다.
- **Quest**: 같은 JSON을 읽고, 기준점 A·B에 둔 공간 앵커로 건물 좌표를 실제 위치에 맞춘다.

| 노드 필드 | 타입 | 설명 |
| --- | --- | --- |
| `id` | string | `z_corridor_3f` 처럼 `z_` 접두어 |
| `type` | enum | `room` · `corridor` · `stair` · `elevator_lobby` · `exit` · `refuge` |
| `name` | string | 화면·리포트용 이름 (예: "3층 복도") |
| `polygon` | \[\[x,z\], …\] | 바닥 외곽선, 반시계 방향 |
| `ceiling_h` | float (m) | 천장 높이, 기본 2.6 |
| `area` | float (m²) | 로드 시 polygon으로 계산 |
| `objects` | \[ObjectRef\] | 소화기·경보 발신기·수건·수도·창문·엘리베이터 버튼 위치 |

| 간선 필드 | 타입 | 설명 |
| --- | --- | --- |
| `id` | string | `e_` 접두어 |
| `from`, `to` | zone id | 방향 없음 |
| `type` | enum | `door` · `opening` · `stair` |
| `width` | float (m) | 통로 폭 |
| `door_open` | bool | 초기 개폐 상태 (`door`만) |
| `handle` | \[x,y,z\] 또는 null | 문 손잡이 위치 (손등 확인 판정용) |
| `side_from_handle` | zone id | 손잡이가 향한 쪽 구역 (훈련자가 서는 쪽) |

```json
{
  "building_id": "sample_building_3f",
  "version": 1,
  "anchors": {"A": [0, 0, 0], "B": [0, 0, 20]},
  "zones": [
    {"id": "z_room_301", "type": "room", "name": "301호", "polygon": [[2,0],[8,0],[8,6],[2,6]], "ceiling_h": 2.6, "objects": [{"kind": "towel", "pos": [3,0.8,1]}]},
    {"id": "z_corridor_3f", "type": "corridor", "name": "3층 복도", "polygon": [[0,0],[2,0],[2,20],[0,20]], "ceiling_h": 2.6, "objects": [{"kind": "alarm", "pos": [0.1,1.2,10]}]},
    {"id": "z_exit_stair_a", "type": "exit", "name": "A계단", "polygon": [[0,20],[2,20],[2,23],[0,23]], "ceiling_h": 2.6, "objects": []}
  ],
  "edges": [
    {"id": "e_301_door", "from": "z_room_301", "to": "z_corridor_3f", "type": "door", "width": 0.9, "door_open": false, "handle": [2.05,1.0,3.0], "side_from_handle": "z_room_301"},
    {"id": "e_stair_a", "from": "z_corridor_3f", "to": "z_exit_stair_a", "type": "door", "width": 1.2, "door_open": false, "handle": [1.0,1.0,19.95], "side_from_handle": "z_corridor_3f"}
  ]
}
```

**파생 값**(로드 시 계산): 각 구역에서 가장 가까운 `exit`까지의 최단 경로 거리(구역 중심 간 거리 합). 평가 항목 "대피 개시"와 NPC 이동에 쓴다. 차단된 간선(`blocked: true`, 시나리오가 설정)은 경로 계산에서 빠진다.

## 4. 화재 엔진

화재 엔진은 구역마다 연기 농도를 10Hz로 갱신하고, 나머지 값(연기층 높이·온도·훈련자 피해)은 농도에서 계산한다. 목표는 물리적 정확도가 아니라 **발화 후 2\~3분 안에 대피하지 않으면 위험해지는 체감 속도**다. 아래 값은 모두 `fire.json`의 초안이다.

**구역 상태**: 연기 농도 D (0\~1). 발화 구역은 추가로 화재 단계를 갖는다.

**갱신 규칙 (dt = 0.1초)**

1. 발화 구역: D가 초당 `source_rate` = 0.04씩 증가 (약 25초에 1.0).
2. 간선마다 농도 차이만큼 흐른다. 흐름은 면적에 반비례해 양쪽에 반영한다.
3. D는 0\~1로 자른다. 발화 후 연기는 줄어들지 않는다(환기 없음).

```latex
\Delta D_{i\to j} = k_{e}\,\max(0,\; D_i - D_j)\,\Delta t \cdot w_e, \qquad D_j \mathrel{+}= \Delta D \cdot \frac{A_{ref}}{A_j}, \quad D_i \mathrel{-}= \Delta D \cdot \frac{A_{ref}}{A_i}
```

| 간선 상태 | k (1/초) |
| --- | --- |
| 열린 문 | 0.3 |
| 닫힌 문 | 0.005 |
| 문 없는 통로 | 0.02 |
| 계단 | 0.20 |
| 간선별 덮어쓰기 `edge_k` | `e_corr_sm` 0.01 (남쪽 복도가 늦게 차도록) |

w는 통로 폭(m), A\_ref = 20m².

**파생 값**

- 연기층 하단 높이: `h_s = ceiling_h − D × (ceiling_h − 0.3)`. D=1이면 바닥 위 30cm까지 내려온다.
- 온도: `T = 20 + 60 × D` (°C). 발화 구역은 `+200`.
- **문 손잡이 온도**: 반대편 구역 T ≥ 60°C이면 "뜨거움". 손등 확인 시 진동·열기 효과로 알려 준다.
- **머리 높이 농도**: 머리가 연기층 안(`head_y > h_s`)이면 `c = D`, 아래면 `c = 0.2 × D`.

**화재 단계 (발화 구역)**

| 단계 | 발화 후 시간 | 소화기 사용 결과 |
| --- | --- | --- |
| 초기 (천장에 닿지 않음) | 0\~30초 | 5초 분사 시 진화, 연기 발생 중단 |
| 성장 | 30\~60초 | 효과 없음 (시도 자체를 평가에서 감점) |
| 최성기 | 60초\~ | 효과 없음, 발화 구역 진입 시 3초 내 실패 |

**훈련자 피해 (FED)**

```latex
FED \mathrel{+}= \frac{c \cdot m_{towel}}{60}\,\Delta t, \qquad m_{towel} = \begin{cases} 0.6 & \text{젖은 수건} \\ 0.8 & \text{마른 수건} \\ 1.0 & \text{없음} \end{cases}
```

두꺼운 연기(c=1) 속에 서 있으면 60초에 FED 1.0이 된다. FED는 줄어들지 않는다.

| FED | 신체 단계 | 효과 (화면·소리) |
| --- | --- | --- |
| 0.30 | 1단계 | 기침 소리, 화면 테두리 흐림 |
| 0.60 | 2단계 | 호흡음 가속, 채도 −50% |
| 0.85 | 3단계 | 시야 축소(비네팅 60%), 시점 흔들림(Quest에서는 끄기) |
| 1.00 | 실패 | 화면 어두워짐 → 결말(실패) |

**연기 표현**: 구역 D와 h\_s로 화면에 연기층을 그린다. 안전 하한으로 연기 불투명도는 최대 0.7, 바닥선과 실제 장애물 윤곽은 항상 보인다(10장).

**튜닝 목표 (확정, 2026-11-11)**: 수치는 바꿔도 되지만 아래 목표는 지켜야 한다. 목표는 `scenario.json`의 `tuning_targets`에 있고, 참조 화재 모델(`contracts/tools/ref_fire.py`)로 CI가 매번 확인한다. 1단계 C# 엔진은 참조 모델과 D 값이 0.01 안에서 같아야 한다.

| 구역 | 목표 (발화 후) | 현재 수치로 결과 |
| --- | --- | --- |
| 북쪽 복도 | 40±5초에 연기층이 1.65m 아래로 | 40초 |
| 중간 복도 | 70±5초에 1.65m 아래로 | 70초 |
| 남쪽 복도 | 150초까지 1.65m 위 유지 | 158초 (1초 간격 측정, 중간 복도는 71초) |

**계산 규칙 보충**: 한 틱의 모든 흐름은 틱 시작 시점의 D로 동시에 계산한다. 출구 구역은 바깥으로 빠지므로 D = 0을 유지한다. 발화 구역 온도는 +200°C여서, 실제로 "뜨거운 문"은 발화 창고 문뿐이다.

**시나리오 입력**(`scenario.json`): 발화 구역, 발화 시각(튜토리얼 끝 후 몇 초), 차단 간선 목록, 초기 문 상태, 문 이벤트(door\_events: 데모에서는 닫힌 창고 문이 발화 38초 뒤 불길에 열림 → 그 전에 손잡이를 확인하면 "뜨거움"을 경험), 튜토리얼 종료 조건, 튜닝 목표, NPC 배치. MVP는 파일 1개, 확장 단계에서 변형을 추가한다.

## 5. 진행 상태 머신

게임 흐름은 `GameFlow` 하나가 상태를 소유한다. 다른 모듈은 상태 변경 이벤트를 구독만 하고, 직접 바꾸지 않는다. **비상 중단은 어느 상태에서든 즉시 `Aborted`로 간다.**

| 상태 | 하는 일 | 다음 상태 (조건) |
| --- | --- | --- |
| `Boot` | 설정·구역 그래프·시나리오 로드, 서버 연결 확인(실패 시 오프라인 모드) | `Calibration` |
| `Calibration` | Quest: 기준점 A·B 앵커 로드, 오차 확인 / 공통: 선 키 측정(머리 높이 3초 평균) | `Tutorial` (요원이 확인 버튼) |
| `Tutorial` | 선택지 대화로 배경 설명, 심부름 부여(교수님 부탁으로 강의실에서 서류 가져오기), 자유 탐색 | `PreFire` (심부름 완료 또는 최대 5분) |
| `PreFire` | 발화 전 대기. 훈련자에게는 튜토리얼과 구분되지 않음 | `Fire` (시나리오의 발화 시각) |
| `Fire` | 화재 엔진 작동, 단서(타는 냄새 소리→연기→경보) 순서대로 노출, NPC 반응 | `Escaped` (`exit` 구역 진입) / `Rescued` (창문 구조 요청 후 30초 버티기) / `Failed` (FED 1.0) / `Timeout` (발화 후 240초) |
| `Ending` | 결말 연출, 연기 농도·경과 시간 공개 | `Report` |
| `Report` | 평가 엔진 실행 → 점수 표시 → LLM 문장 도착 시 교체 | `PreFire` (재시도: 새 run, 튜토리얼 생략) / 종료 |
| `Aborted` | 모든 효과 즉시 끄기, 패스스루만 표시, 로그 저장 | 종료 |

**단서 순서**(`Fire` 진입 기준): 0초 타는 소리·냄새 자막 → 발화 구역 밖으로 연기가 보이기 시작 → 60초 건물 경보(훈련자가 먼저 발신기를 누르면 그때). 첫 단서 노출 시각은 `CUE_EXPOSED` 이벤트로 남긴다.

## 6. 행동 로그

로그는 세션 → 시도(run) → 이벤트 3단이다. 클라이언트는 이벤트를 메모리와 로컬 파일(JSON Lines)에 먼저 쓰고, 2초마다 묶어 서버로 보낸다. 전송에 실패해도 평가는 로컬 로그로 한다.

**모든 이벤트의 공통 필드**

| 필드 | 타입 | 설명 |
| --- | --- | --- |
| `run_id` | uuid |  |
| `seq` | int | run 안에서 1부터 증가 |
| `t` | float (초) | run 시작부터 경과 시간 |
| `type` | enum | 아래 표 |
| `zone` | zone id | 훈련자가 있는 구역 |
| `pos` | \[x,y,z\] | 건물 좌표(m), 머리 위치 |
| `head_ratio` | float | 현재 머리 높이 ÷ 선 키 |
| `c_head` | float | 머리 높이 연기 농도 |
| `fed` | float | 누적 FED |
| `data` | object | 이벤트별 내용 |

**추가 공통 필드 (0단계 확정)**: `zone_d`(현재 구역의 연기 농도 D), `layer_y`(현재 구역의 연기층 높이). 코·입 보호와 낮은 자세를 로그만으로 판정하기 위해 필요하다. 이벤트 data도 `TOWEL_EQUIP.on`(착용/해제), `ALARM_PRESSED.object`를 추가했다. 전체 형식은 `contracts/schemas/log_event.schema.json`이 기준이다.

**이벤트 목록**

| type | 언제 | data |
| --- | --- | --- |
| `RUN_START` / `RUN_END` | run 시작·끝 | `scenario_id` / `outcome`: escaped·rescued·failed·timeout·aborted |
| `STATE_CHANGE` | GameFlow 상태 변경 | `from`, `to` |
| `CUE_EXPOSED` | 화재 단서가 처음 보이거나 들림 | `cue`: sound·smell·smoke·alarm |
| `ZONE_ENTER` | 구역 이동 | `from`, `to`, `exit_dist`(출구까지 거리) |
| `POSTURE` | head\_ratio가 0.7 기준선을 넘나들 때 | `low`: bool |
| `SAMPLE` | 1초마다 (평가·타임라인용) | 없음 (공통 필드만) |
| `HANDLE_CHECK` | 손등으로 문 손잡이 확인 | `edge`, `hot`: bool |
| `DOOR_OPEN` / `DOOR_CLOSE` | 문 열기·닫기 | `edge`, `hot`: bool, `checked_before`: bool |
| `ALARM_PRESSED` | 가상 경보 발신기 누름 |  |
| `SHOUT` | "불이야" 선택지(확장: 음성) | `method`: choice·voice |
| `PICKUP` / `USE` | 사물 줍기·사용 | `item`: extinguisher·towel·bag·phone…, `essential`: bool |
| `TOWEL_WET` / `TOWEL_EQUIP` | 수건 적시기(수도 오브젝트) / 코·입에 대기 |  |
| `EXTINGUISH` | 소화기 분사 시작·끝 | `phase`: 초기·성장·최성기, `duration`, `success` |
| `ELEVATOR_CALL` | 엘리베이터 버튼 |  |
| `WINDOW_SIGNAL` | 창문에서 구조 요청 |  |
| `NPC_INTERACT` | NPC에게 알리기·부축·지나침 | `npc`, `action`: warn·escort·ignore |
| `CROWD_FOLLOW` | 군중을 따라 엘리베이터 로비로 10초 이상 이동 |  |
| `BODY_STAGE` | 신체 단계 변경 | `stage`: 1·2·3 |
| `ABORT` | 비상 중단 | `by`: staff·system |

"지나침(ignore)"은 NPC가 훈련자 2m 안에 3초 이상 있었는데 상호작용 없이 멀어졌을 때 시스템이 기록한다.

## 7. 평가 규칙

평가 엔진은 run이 끝나면 이벤트 로그만 입력으로 받아 항목별 등급과 점수를 돌려준다(순수 함수, 같은 로그면 같은 결과). 기준 시각 **t0 = 첫 `CUE_EXPOSED`**. 등급은 좋음·보통·미흡·해당 없음 4가지이고, 총점은 해당 항목 배점 합 대비 백분율로 낸다. 구간 값은 `evaluation.json` 초안이고, 근거 조항은 원문 대조 후 확정한다(12장).

| # | 항목 (id) | MVP | 측정식 | 좋음 | 보통 | 미흡 | 배점 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 화재 알리기 (`alert`) | O | t0부터 첫 `ALARM_PRESSED` 또는 `SHOUT`까지 시간 | ≤30초 | ≤60초 | 없음·초과 | 10 |
| 2 | 대피 개시 지연 (`evac_delay`) | O | t0부터 `exit_dist`가 5초 연속 줄어들기 시작한 시각까지 | ≤30초 | ≤60초 | >60초 | 15 |
| 3 | 엘리베이터 미사용 (`no_elevator`) | O | `ELEVATOR_CALL` 횟수 | 0회 | — | 1회 이상 | 10 |
| 4 | 문 손잡이 확인 (`door_check`) | O | t0 이후 `DOOR_OPEN` 중 `checked_before`인 비율. 뜨거운 문을 열면 즉시 미흡 | 100% | ≥50% | <50% 또는 뜨거운 문 개방 | 10 |
| 5 | 코·입 보호 (`nose_mouth`) | O | c\_head ≥ 0.3 구역에 처음 들어갈 때 수건 상태 | 젖은 수건 | 마른 수건 | 없음 | 10 |
| 6 | 연기 속 자세 (`low_posture`) | O | h\_s < 선 머리 높이인 `SAMPLE` 중 head\_ratio ≤ 0.7인 비율 | ≥80% | ≥50% | <50% | 15 |
| 7 | 초기 소화 판단 (`extinguish`) | — | 초기 단계 사용 / 성장 이후 시도 시간 | 초기에 사용 | 미사용 | 성장 이후 10초 이상 시도 | 10 |
| 8 | 대피 불가 시 대응 (`blocked_response`) | — | 차단 간선에 막힌 뒤 `WINDOW_SIGNAL` 또는 `refuge` 진입 | 60초 안 | 120초 안 | 없음 | 10 |
| 9 | 소지품 수집 지연 (`belongings`) | O | t0 이후 `essential=false`인 `PICKUP` 전후 머문 시간 합 | 0초 | ≤10초 | >10초 | 10 |
| 10 | 타인 알리기·동행 (`warn_others`) | O | 이름 있는 NPC 중 warn 또는 escort한 비율 | 100% | >0% | 0% | 10 |

**해당 없음 규칙**: 조건이 생기지 않은 항목은 총점 계산에서 빼다. 예: 문을 한 번도 열지 않음(4), 연기 구역에 들어가지 않음(5·6), 차단되지 않음(8).

**0단계 확정 사항** (정확한 알고리즘은 `evaluation.json`의 항목별 `measure`와 참조 평가기 `contracts/tools/ref_eval.py`가 기준)

- `belongings`: 시간 대신 t0 이후 비필수 아이템을 주운 **개수**로 판정한다. 0개 좋음, 1개 보통, 2개 이상 미흡.
- `evac_delay`: 1초 표본의 연속 출구 거리에서, 첫 걸음이 0.1m 이상 줄고 5초 동안 0.05m 넘게 늘지 않으며 1.5m 이상 줄거나 출구에 도착한 첫 시각을 대피 시작으로 본다.
- 총점: floor(100 × 득점 합 ÷ 해당 항목 배점 합 + 0.5). MVP 단계(`mvp_only`)에서 비MVP 2개 항목은 해당 없음.
- 각 항목의 문장 틀(`text`)은 이미 `evaluation.json`에 초안이 있다. 근거 원문(`rules.*.quote`)만 사람이 채우면 된다.

**등급 → 점수**: 좋음 100%, 보통 50%, 미흡 0% × 배점. 결말(탈출 여부)은 점수와 따로 표시한다.

**출력 형식** (LLM 리포트의 입력이기도 하다)

```json
{
  "run_id": "…",
  "outcome": "escaped",
  "total_pct": 72,
  "elapsed_s": 131.4,
  "items": [
    {"id": "evac_delay", "grade": "normal", "points": 7.5, "max": 15,
     "evidence": {"delay_s": 48.2}, "rule_ref": "FIRE-04"}
  ],
  "timeline": [{"t": 0, "c_head": 0.0, "zone": "z_room_301"}],
  "weak_zones": ["z_corridor_3f"]
}
```

`weak_zones`는 훈련자가 가장 오래 머문 연기 구역 상위 2개다("이 건물의 대피 취약 지점"의 재료). `rule_ref`는 `evaluation.json`의 근거 조항 테이블 키다.

## 8. NPC와 LLM 연동

LLM은 두 곳에만 쓴다: NPC의 다음 행동·대사 정하기, 평가 결과를 문장으로 쓰기. 둘 다 서버가 중계하고 API 키는 팀장 계정으로 발급하고(월 사용 한도 설정), 서버 환경 변수 \`ANTHROPIC\_API\_KEY\`에만 둔다. 저장소에는 \`.env.example\`만 올린다. 두 호출 모두 구조화 출력(JSON 스키마)을 쓰고, 스키마 검증에 실패하면 대체 경로로 간다.

### NPC 반응

| 항목 | 값 |
| --- | --- |
| 모델 | `claude-haiku-5-5`, effort `low` |
| 호출 시점 | NPC 반경 3m 안에서 `NPC_INTERACT` 발생, 또는 NPC 구역의 연기 등급이 바뀜 때 |
| 순서 | ① 로컬 캐시 조회 → ② 없으면 서버 `/npc/react` (타임아웃 3초) → ③ 실패·지연이면 예비 대사 |
| 행동 집합 | `evacuate` · `follow_player` · `freeze` · `ask_help` · `go_elevator` · `hide` |
| 대사 | 한국어 12어절 이내, 행동요령을 가르치지 않음(정답을 대신 말하지 않기) |
| 캐시 키 | `npc_id \| 훈련자 행동(warn·escort·ignore·none) \| 연기 등급(0~3) \| NPC 현재 행동` |
| 캐시 생성 | `scripts/build_npc_cache.py`가 키 조합 전부(약 2명×4×4×6 = 192개)를 미리 생성해 `npc_cache.json`으로 빌드에 포함. 사람이 검토 후 커밋 |

입력(서버가 프롬프트로 변환):

```json
{"npc": {"id": "npc_minji", "persona": "…", "action": "freeze", "zone": "z_room_302"},
 "player_action": "warn", "smoke_level": 2, "recent_events": ["DOOR_OPEN e_302_door", "SHOUT"]}
```

출력 스키마:

```json
{"action": "follow_player", "line": "알았어, 나도 갈게!", "emotion": "scared"}
```

연기 등급: 0 = c<0.1, 1 = <0.3, 2 = <0.6, 3 = ≥0.6.

**0단계 확정**: 실제 요청은 평탄한 형태다 — `npc_id, persona, current_action, zone, player_action, smoke_level, recent_events` (`server/app/schemas.py`의 `NpcReactRequest`). 대화 선택지 `warn`은 `NPC_INTERACT`와 함께 `SHOUT(method=choice)`도 남겨 "화재 알리기"로 인정한다.

### 평가 리포트 문장

| 항목 | 값 |
| --- | --- |
| 모델 | `claude-sonnet-5-5`, effort `medium`, 거절 시 서버 측 fallback(`fallbacks: "default"`) 사용 |
| 입력 | 7장의 평가 결과 JSON + 항목별 행동요령 원문 |
| 출력 | `summary`(3\~4문장) + 항목별 `good` / `improve` / `guideline` |
| 규칙 | 점수·등급을 바꾸거나 새로 판단하지 않음. 원문에 없는 행동요령을 만들지 않음 |
| 타임아웃 | 10초. 넘으면 클라이언트가 `evaluation.json`의 문장 틀로 만든 리포트를 그대로 보여줌 |
| 표시 | 점수·표는 즉시, 문장은 도착하면 교체 |

검증: 서버는 출력의 항목 id 집합이 입력과 같은지 확인하고, 다르면 문장 틀 리포트로 대체한다.

### 프롬프트 파일

`server/prompts/npc_system.md`, `report_system.md`에 둔다. 캐시 적중을 위해 시스템 프롬프트는 고정하고, 변하는 값은 user 메시지에만 넣는다.

## 9. 서버 API와 DB

서버는 저장, LLM 중계, 비상 중단 전달 세 가지만 한다. 시연 노트북에서 실행하고, Quest·요원 폰은 같은 Wi-Fi 또는 노트북 핫스팟으로 접속한다(서버는 \`0.0.0.0:8000\`, 클라이언트는 설정 파일의 IP 사용). 모든 요청은 `X-Device-Token` 헤더(기기별 고정 토큰, `settings.py`)을 검사한다. 응답은 JSON, 오류는 `{"error": {"code", "message"}}`.

| 메서드 · 경로 | 요청 | 응답 | 비고 |
| --- | --- | --- | --- |
| `GET /health` | — | `{ok, llm: bool}` | Boot에서 오프라인 여부 판단 |
| `GET /buildings/{id}/zone-graph` | — | zone\_graph.json | 클라이언트도 같은 파일을 내장 |
| `POST /sessions` | `{building_id, participant_code, device}` | `{session_id}` | 참가자는 코드만(실명 없음) |
| `POST /sessions/{id}/runs` | `{scenario_id, attempt}` | `{run_id}` |  |
| `POST /runs/{id}/events` | `{events: […]}` | `{accepted}` | `seq` 중복은 무시(멱등) |
| `POST /runs/{id}/finish` | `{outcome, evaluation}` | `{report_id}` | 클라이언트 평가 결과 저장 |
| `POST /runs/{id}/report` | — | 리포트 문장 JSON | 내부에서 Sonnet 호출, 10초 |
| `POST /npc/react` | 8장 입력 | 8장 출력 | 내부에서 Haiku 호출, 3초 |
| `WS /ws/device/{device}` | — | `{type: "abort"}` 등 | 클라이언트가 상시 연결 |
| `POST /staff/abort/{device}` | — | `{ok}` | 요원 화면의 중단 버튼 |
| `GET /staff` | — | HTML | 요원용 한 화면: 기기 상태·FED·중단 버튼 |
| `GET /runs/{id}/export` | — | CSV | 사용자 실험 분석용 |

**DB 테이블**

| 테이블 | 주요 컬럼 |
| --- | --- |
| `buildings` | id, name, zone\_graph(JSON), version |
| `sessions` | id, building\_id, participant\_code, device, started\_at |
| `runs` | id, session\_id, scenario\_id, attempt, outcome, started\_at, ended\_at |
| `events` | run\_id, seq, t, type, zone, pos(JSON), head\_ratio, c\_head, fed, data(JSON) — PK(run\_id, seq) |
| `evaluations` | run\_id, total\_pct, items(JSON), weak\_zones(JSON) |
| `reports` | run\_id, model, text(JSON), latency\_ms, fallback\_used |
| `llm_calls` | id, kind(npc·report), latency\_ms, input\_tokens, output\_tokens, ok |

`llm_calls`는 정량 목표(NPC 3초, 리포트 10초) 측정에 쓴다.

## 10. 입력·자세·안전

**플랫폼별 입력**

| 동작 | PC 프로토타입 | Quest 3 (손 추적) |
| --- | --- | --- |
| 이동 | WASD + 마우스 | 실제 보행 |
| 자세 낮추기 | C 키 토글 (머리 1.65m ↔ 0.9m) | 실제 머리 높이 |
| 사물 잡기·사용 | E 키 (시점 중앙 1.5m 이내) | 집기(pinch) |
| 문 손잡이 확인 | H 키 (손잡이 1m 이내) | 손등이 손잡이 8cm 이내에 0.5초 (손바닥이 손잡이 반대쪽을 향함) |
| 문 열기·닫기 | F 키 | 가상 문은 손잡이를 집어 당기기. 실제 문은 훈련자가 열고, 문 앞 통과로 감지 |
| 수건 코·입에 대기 | T 키 | 수건을 쥐고 얼굴 15cm 이내 |
| 선택지 대화 | 마우스 클릭 | 검지 포인터로 누르기 |

**자세**: `Calibration`에서 선 머리 높이를 재고 head\_ratio = 현재/선 높이로 쓴다. 키가 달라도 같은 기준(0.7)으로 판정한다.

**실제 문 처리 (Quest)**: 실제 문의 개폐는 감지할 수 없다. 훈련자가 문 폭 안을 통과하면 `DOOR_OPEN`으로 기록하고 화재 엔진에서도 열린 것으로 본다. MVP Quest 데모의 복도에서는 가상 문만 쓴다.

**안전 요구사항** (기능 요구사항으로 다루고, 테스트로 확인한다)

1. 연기 불투명도 상한 0.7. 바닥 경계선과 MRUK 메시의 장애물 윤곽선은 연기 위에 그린다.
2. 화면 흔들림·급격한 밝기 변화는 Quest에서 쓰지 않는다(멀미·낙상 방지).
3. 비상 중단: 요원 화면(`/staff`) 버튼 → 웹소켓 → 1초 안에 `Aborted`. 웹소켓이 5초 끊기면 훈련을 일시정지하고 효과를 끈다.
4. Quest 경계(Guardian) 해제는 복도 데모에서만 쓰고, 안전요원이 항상 동행한다.
5. 계단 구역은 MVP에서 훈련 범위에 넣지 않는다. `exit` 구역은 계단참 문 앞에서 끝난다.
6. 시작 전 안내 화면에 실패 연출과 중단 방법을 알리고, 요원이 확인해야 시작한다.

## 11. 일정과 완료 기준

남은 기간은 5주(11/11\~12/18)다. AI는 PC에서 돌아가는 코드를 만들고, 사람은 콘텐츠와 Quest 작업을 맡는다. **Quest 3가 아직 없으므로, W12(11/24)까지 확보하지 못하면 Quest 복도 데모는 12/18 범위에서 빼고 PC 완성도에 집중한다.** 확보하면 사람이 기기에서 확인한 결과(스크린샷·fps·오차 수치)를 AI에게 넘겨주는 반복 구조로 진행한다.

| 주차 | AI 구현 (15장 단계) | 사람 | 주말 확인 기준 |
| --- | --- | --- | --- |
| W11 (11/11\~17) | **0 계약 ✅** → 1 Core → 2 서버(가짜 LLM) | PR #1 리뷰·병합, API 키 발급 | Core·서버 테스트 통과, C# 평가가 참조 평가기와 일치, C# 화재 엔진이 참조 모델과 일치 |
| W12 (11/18\~24) | 3 걸을 수 있는 데모 → 4 화재 데모 시작 | Quest 확보 결정(11/24), 한글 폰트, 행동요령 원문 | PC에서 걸어서 출구까지, 로그가 파일·서버에 쌓임 |
| W13 (11/25\~12/1) | 4 화재 데모 → 5 리포트 | 소리·이펙트, 대본 검토, 연기 표현 피드백 | 자동 플레이 2종 결과가 기대값과 일치 |
| W14 (12/2\~8) | 6 NPC → 7 튜토리얼·UI | NPC 대사 검토, 팀원 테스트 1차 | 시작부터 리포트까지 끊김 없음, Esc 중단 |
| W15 (12/9\~15) | 8 다듬기 | 팀원 테스트 2차, 보고서·발표 | 16.13 체크리스트, 60fps |
| 12/16\~18 | 여유분 | 제출·발표 |  |

3·4단계는 AI가 화면을 볼 수 없는 구간이다. W12\~W13에는 사람이 짧게라도 매일 플레이하고 스크린샷으로 피드백한다. Quest 작업은 기기를 확보한 경우에만 W14에 끼워 넣는다.

10명 이상 사용자 실험(동의서·사전/사후 퀴즈 포함)은 12/18 이후로 미룬다.

**모듈별 완료 기준** (AI는 이것을 테스트로 먼저 만든다)

| 모듈 | 완료 기준 |
| --- | --- |
| 구역 그래프 | 예시 JSON 로드, 면적·출구 거리 계산, 차단 간선 제외 경로가 맞음. 잘못된 JSON은 어떤 필드가 틀렸는지 오류로 알려줌 |
| 화재 엔진 | 참조 모델(ref\_fire.py)과 구역별 D 0.01 이내, 4장 튜닝 목표(tuning\_targets) 충족. 닫힌 문 뒤 구역은 150초에 0.2 미만. 같은 입력이면 같은 결과 |
| FED·신체 단계 | c=1에서 60초에 실패, 젖은 수건이면 100초. 단계 이벤트가 정확한 임계값에서 한 번씩 발생 |
| 평가 엔진 | 모범 로그·실패 로그·해당 없음 로그 3종에서 항목별 등급이 기대값과 일치 |
| GameFlow | 모든 상태에서 비상 중단 시 1초 안에 `Aborted`. 재시도는 새 run\_id |
| 서버 | pytest로 전 엔드포인트 통과. LLM을 끄면 모든 호출이 대체 경로로 응답 |
| NPC | 캐시 적중 시 0.5초, 런타임 3초 초과 시 예비 대사. 출력이 행동 집합 밖이면 거부 |
| 리포트 | 종료 후 10초 안에 표시(문장 틀 포함). LLM 문장이 점수를 바꾸지 않음 |
| Quest 데모 | 복도 1곳에서 발화→대피→리포트까지 끊김 없이 진행, 평균 72fps, 기준점 오차 30cm 이내 (사람이 측정) |

## 12. 미결정 사항 (사람이 판단)

아래는 AI가 정하면 안 되는 항목이다. 정해진 것은 표로 옮기고, 남은 것은 괄호 안 임시값으로 AI가 진행한다.

**결정됨 (2026-11-11)**

| 항목 | 결정 | 반영 위치 |
| --- | --- | --- |
| 훈련 건물 | 미정. 예시 구조(복도 1 + 방 2 + 출구 1)로 구현하고, 정해지면 `zone_graph.json`과 그레이박스 씬만 교체 | 3장 |
| 근거 원문 | 국민재난안전포털(행정안전부)을 기본으로 소방청 자료와 대조. `rule_ref`마다 출처·문구를 함께 저장 | 7장 |
| 튜토리얼 | 교수님 부탁으로 강의실에서 서류를 가져오는 심부름 | 5장 |
| NPC 2명 | 얼어붙는 후배(알리면 따라옴), 엘리베이터로 가려는 동기(말리지 않으면 엘리베이터 로비로) | 8장 |
| 실패 연출 | 계획서대로 3단계 전부. Quest에서는 화면 흔들림만 끔 | 4장 |
| 사용자 테스트 | 12/18 전에는 팀원끼리. 10명 실험은 이후 | 11장 |
| Quest 3 | 아직 없음. W12까지 못 구하면 Quest 데모 제외 | 11장 |
| 서버 위치 | 시연 노트북, 같은 Wi-Fi 또는 핫스팟 | 9장 |
| API 키 | 팀장이 발급·관리, 월 한도 설정 | 8장 |

**추가 결정 (2026-11-11 오후, 나중에 바꿀 수 있음 — 17장 참고)**

| 항목 | 결정 | 바꾸는 곳 |
| --- | --- | --- |
| 개발 환경 | Unity 6000.6.8f1, 저장소 `Ppangdo/FireDrill-AR`(private, `C:\dev\FireDrill-AR`), GitHub Actions CI | `CLAUDE.md` |
| 화재 튜닝 목표 | 북쪽 40초, 중간 70초, 남쪽 150초까지 안전 | `scenario.json` `tuning_targets` |
| 뜨거운 문 장면 | 창고 문을 닫힌 채 시작, 발화 38초 뒤 불길에 열림 | `scenario.json` `initial_doors`, `door_events` |
| 튜토리얼 → 화재 | 서류를 가진 채 302호에 들어오면 `errand_return` 대화 후 PreFire (최대 300초) | `scenario.json` `tutorial` |
| PC 보정 단계 | 건너뜀, 선 키 1.65m 고정 | `app_config.json` `skip_calibration` |

**콘텐츠·교육 기준**

- [ ] 7장 판정 구간(30초·60초, 자세 80% 등)이 교육적으로 타당한가? 지도교수 자문을 받을지? (임시: 표의 값)
- [ ] 배점(대피 개시·자세 15점, 나머지 10점)을 이대로 둘까? (임시: 이대로)
- [ ] NPC 2명의 이름과 대사 말투는? (임시: 후배 "민지", 동기 "준호", 반말)
- [ ] 튜토리얼 대사 원고는 팀장이 쓸까, AI 초안을 고칠까? (임시: AI 초안)

**실험·운영**

- [ ] 훈련 건물은 언제까지 정하고, 평면도·실측은 누가 하나? 사용 허가는 누가 받나?
- [ ] 12/18 이후 10명 실험에 동의서·IRB가 필요한가? 학교 기준 확인

**자원·예산**

- [ ] **Quest 3를 어디서 몇 대 구하나?** 학과 대여, 연계기업 다빈소프트, 예산 구매 중 확인 (기한: W12, 11/24)
- [ ] API 월 사용 한도 금액은? (임시: $20)
- [ ] 불·연기 VFX와 NPC 캐릭터 모델은 어디서 구하나? (임시: 무료 에셋, 부족하면 구매 15만 원 안)
- [ ] 다빈소프트에 물어볼 것: ① Quest 3 등 장비 지원 가능 여부 ② 평가 기준·리포트 형식 자문 가능 여부 ③ 요원 화면 구성 조언 ④ 회사가 원하는 기능이나 결과물 형식 (13장 참고)

**기기에서만 알 수 있는 것** (사람이 측정해 알려주면 AI가 수치를 고침)

- [ ] 복도 길이에서 앵커 오차가 실제로 몇 cm인가
- [ ] 경계 해제가 우리 기기·계정에서 되는가
- [ ] 패스스루 위 연기가 연기처럼 보이는가, 어떤 농도부터 시야가 너무 가려지는가
- [ ] 손등 확인 동작이 자연스럽게 인식되는가(8cm·0.5초 조정)

## 13. 연계기업(다빈소프트) 활용

다빈소프트는 산업 설비 감시·제어 소프트웨어 회사다. 공개 자료에 Unity·XR·LLM 경험은 없으므로 **기술 스택은 바꾸지 않는다.** 대신 훈련 평가 경험, 실시간 감시 화면, 설비 연계 세 가지를 산학 연결고리로 쓴다. (조사 기준일 Nov 11, 2026, 공개 웹 자료만 확인)

**확인된 기술** (사용 언어·OS·엔진은 비공개)

| 제품 | 하는 일 | 기술 |
| --- | --- | --- |
| [DataMon](https://www.davinsoft.com/en/products/datamon/) | 설비 실시간 감시·제어·시각화 (주력) | OPC UA/DA, Modbus, IEC 61850, PLC·CNC 드라이버 / MySQL·MariaDB·MS-SQL·Oracle·시계열 DB / 편집형 대시보드 |
| [OPC UA Server](https://www.davinsoft.com/en/products/opc-server/) | 제어 장비 데이터를 OPC UA로 변환 | 2ms 샘플링, X.509 보안 |
| [SecureNet](https://www.davinsoft.com/en/products/securenet/) | 물리적 단방향 보안 통신 | 국정원 EAL2 |
| [AI 기반 전차 사격 훈련 체계](https://www.davinsoft.com/en/solutions/defense-industry/) | 실전차 비사격 훈련, 육군과 특허 공동 출원, 상용화 중 | "AI 영상 인식"만 공개 |

**적용 계획**

| 연결고리 | 우리 과제의 대상 | 시기 | 필요한 것 |
| --- | --- | --- | --- |
| 훈련 평가 자문 | 7장 판정 구간·배점, 8장 리포트 형식 | W12\~W13 | 7장 표와 리포트 예시를 검토 요청 |
| 감시 화면 조언 | 9장 `/staff` 요원 화면, 건물 취약 지점 화면 | W13 | 화면 시안 1장 |
| 설비 연계 (읽기 전용) | 확장 단계: OPC UA로 화재수신기·건물 관리 시스템 데이터를 받아 시나리오에 사용. 실제 설비에는 절대 쓰지 않음(단방향) | 12/18 이후, 보고서에는 확장 계획으로 기술 | 회사 의사 확인 |
| DB 호환 | 9장 DB | 회사가 운영을 맡을 때만 | SQLModel 연결 문자열만 MariaDB·MySQL로 교체 |
| 장비 지원 | Quest 3 확보 (11장) | W11 문의, W12 기한 | 담당자 문의 |

**AI 작업 규칙**: 다빈소프트 관련 기능은 12/18 범위에 없다. OPC UA 연계는 사람이 승인하기 전까지 구현하지 않는다. 요원 화면은 회사 조언이 오면 반영할 수 있게 화면 구성을 한 파일(`server/app/static/staff.html`)에 모아 둔다.

Sources: [다빈소프트 홈페이지](https://www.davinsoft.com/en/) · [경남ICT협회 기업탐방 (2022)](https://www.gnict.org/%EA%B2%8C%EC%8B%9C%ED%8C%90/%EA%B8%B0%EC%97%85%ED%83%90%EB%B0%A9/%EC%9E%91%EC%A7%80%EB%A7%8C-%EC%84%B1%EC%9E%A5%EC%A4%91%EC%9D%B8-%ED%95%B5%EC%8B%AC-%EA%B8%B0%EC%88%A0%EA%B8%B0%EC%97%85-%EB%8B%A4%EB%B9%88%EC%86%8C%ED%94%84%ED%8A%B8/?ro=created_at)

## 14. 모듈 계약

이 장은 모듈끼리 맞물리는 지점을 고정한다. **다른 장과 다르면 이 장이 우선한다.** 저장소의 첫 커밋은 이 장을 코드로 옮긴 **계약 파일**(C# 인터페이스, JSON 스키마, Python 모델, 예시 데이터)이고, 이후 모든 모듈은 이 파일에 맞춰 만든다. 계약을 바꿀 때는 이 장과 계약 파일을 같은 커밋에서 고친다.

**계약의 기준 위치 (0단계 이후)**: 이 장은 요약이고, 필드 하나하나의 기준은 저장소다. 두 곳이 다르면 저장소가 맞다.

| 내용 | 저장소 경로 |
| --- | --- |
| JSON 형식 | `contracts/schemas/*.schema.json` (11종) |
| 데모 설정 | `unity/Assets/FireDrill/Config/*.json` (7종) |
| C# 계약 | `unity/Assets/FireDrill/Core/Contracts/`, `Runtime/Contracts/` |
| 서버 계약 | `server/app/schemas.py` |
| 참조 구현 | `contracts/tools/ref_eval.py`(평가), `ref_fire.py`(화재) |

0단계에서 더해진 필드: 로그 `zone_d`·`layer_y`, 간선 `center`·`wall_axis`, `fire.edge_k`, 시나리오 `retry_spawn`·`door_events`·`tutorial`·`tuning_targets`·NPC `home_zone`, `app_config.skip_calibration`, `ZoneGraph.ExitDistance`의 반환형 `float?`, `FlowState` enum.

### 14.1 공통 규약

| 항목 | 규약 |
| --- | --- |
| 단위 | 길이 m, 시간 초, 온도 °C, 농도·FED는 0\~1 실수 |
| 좌표 | 건물 좌표(3장)는 Y축 위·오른손 무관한 Unity 좌표와 같다. PC는 건물 좌표 = 월드 좌표. Quest는 `ISpaceProvider`의 행렬 하나로 변환 |
| 2D 좌표 | 바닥 평면은 \[x, z\] |
| 시간 | Core의 모든 시간은 **run 시간 t**(run 시작 = 0, 일시정지 중 멈춤). 발화 시각 `t_fire`, 평가 기준 `t0` = 첫 `CUE_EXPOSED`의 t |
| 틱 | 화재 엔진·노출(FED)은 고정 0.1초. Runtime이 프레임 시간을 누적해 0.1초마다 호출 |
| ID | 구역 `z_`, 간선 `e_`, 오브젝트 `o_`, NPC `npc_`, 시나리오 `s_`, 모두 소문자 snake\_case |
| JSON | 필드는 snake\_case. C#은 Newtonsoft.Json(`com.unity.nuget.newtonsoft-json`) + SnakeCaseNamingStrategy, Python은 Pydantic v2 |
| 버전 | 모든 JSON 파일과 API 본문에 `"schema_version": 1` |
| enum 문자열 | JSON에는 snake\_case 문자열(예: `door_open`), C#에서는 PascalCase enum |
| 로그 파일 | `Application.persistentDataPath/runs/{run_id}.jsonl`, 한 줄에 이벤트 하나 |

### 14.2 Core 계약 (C#, `FireDrill.Core`, UnityEngine 참조 금지)

```csharp
namespace FireDrill.Core {
  public readonly struct Vec3 { public readonly float X, Y, Z; /* + 연산자, Distance */ }
  public readonly struct Vec2 { public readonly float X, Z; }

  // ── 공간 ──
  public sealed class ZoneGraph {
    public static ZoneGraph Load(string json);           // 스키마 오류 시 ZoneGraphException(필드 경로 포함)
    public IReadOnlyList<Zone> Zones { get; }
    public IReadOnlyList<Edge> Edges { get; }
    public Zone Zone(string zoneId);
    public IEnumerable<Edge> EdgesOf(string zoneId);
    public string ZoneAt(Vec3 pos);                       // 바닥 다각형 포함 검사, 밖이면 null
    public void SetBlocked(string edgeId, bool blocked);
    public float ExitDistance(Vec3 pos);                  // 16.8의 연속 출구 거리
    public IReadOnlyList<Vec3> PathToExit(Vec3 from);     // 문 중심을 잇는 경유점 (NPC용)
    public IReadOnlyList<Vec3> PathTo(Vec3 from, string zoneId);
  }

  // ── 화재 ──
  public enum FirePhase { None, Initial, Growing, Full, Extinguished }
  public readonly struct ZoneState { public readonly float D, SmokeLayerY, TempC; }
  public interface IFireEngine {
    void Reset(ZoneGraph graph, FireConfig cfg, Scenario scenario);
    void Ignite(float t);                                 // GameFlow가 Fire 진입 시 호출
    void Tick(float t, float dt);                         // dt = 0.1 고정
    FirePhase Phase { get; }
    ZoneState Get(string zoneId);
    void SetDoor(string edgeId, bool open);
    bool IsDoorHot(string edgeId, string fromZoneId);     // 반대편 TempC ≥ cfg.hot_door_temp_c
    float HeadConcentration(Vec3 head);                    // 4장의 c
    ExtinguishResult Extinguish(float t, float sprayedSeconds, float distance);
  }
  public enum ExtinguishResult { Ineffective, InProgress, Extinguished }

  // ── 노출 ──
  public enum TowelState { None, Dry, Wet }
  public sealed class Exposure {
    public float Fed { get; }
    public int Stage { get; }                             // 0~3, FED 1.0이면 4(실패)
    public event Action<int> StageChanged;
    public void Accumulate(float c, TowelState towel, float dt);
  }

  // ── 로그 ──
  public readonly struct Snapshot { public readonly float T; public readonly string Zone; public readonly Vec3 Pos;
                                    public readonly float HeadRatio, CHead, Fed; }
  public sealed record LogEvent(string RunId, int Seq, float T, string Type, Snapshot S, JObject Data);
  public interface IEventLog {
    LogEvent Append(string type, Snapshot s, object data = null);   // seq는 내부에서 증가
    IReadOnlyList<LogEvent> Events { get; }
    event Action<LogEvent> Appended;                                 // 서버 업로더·파일 기록이 구독
  }

  // ── 평가 ──
  public static class Evaluator {
    public static EvaluationResult Evaluate(IReadOnlyList<LogEvent> events, ZoneGraph graph,
                                            Scenario scenario, EvaluationConfig cfg);   // 순수 함수
    public static ReportText TemplateReport(EvaluationResult r, EvaluationConfig cfg);    // LLM 대체 문장
  }
}
```

`FireConfig`, `Scenario`, `EvaluationConfig`, `EvaluationResult`, `ReportText`는 14.5·7장 JSON과 1:1인 읽기 전용 클래스다. 이벤트 `type` 문자열은 6장 표의 값을 `EventTypes` 상수 클래스에 모아 쓴다.

### 14.3 플랫폼 어댑터 계약 (`FireDrill.Runtime`)

```csharp
public interface IPlayerRig {            // PC: 캐릭터 컨트롤러, Quest: 헤드셋
  Vec3 Head { get; } Vec3 Forward { get; } float StandingHeight { get; }
  void Teleport(Vec3 feet, float yawDeg); // 재시도용 (Quest에서는 안내만)
}
public enum IntentKind { Use, Grab, Drop, HandleCheck, ToggleDoor, TalkTo, TowelToggle, Abort }
public readonly struct Intent { public readonly IntentKind Kind; public readonly string TargetId; }
public interface IInteractionSource {    // PC: 키보드+시점 레이캐스트, Quest: 손 추적
  event Action<Intent> IntentRaised;     // 대상 판별까지 끝낸 결과만 올린다
  string FocusedTargetId { get; }        // 상호작용 표식용, 없으면 null
}
public interface ISpaceProvider { bool Ready { get; } Matrix4x4 BuildingToWorld { get; } }
```

### 14.4 Runtime 구성 요소와 소유권

상태를 바꾸는 주인은 하나다. 다른 요소는 C# 이벤트를 구독만 한다. 모든 로그는 `RunRecorder.Record(type, data)` 하나로만 남긴다.

| 요소 | 하는 일 | 소유하는 상태 | 의존 |
| --- | --- | --- | --- |
| `Bootstrap` | 설정 로드, 어댑터 선택(PC/Quest), 모든 요소 생성·연결 | 없음 | 전부 |
| `GameFlow` | 5장 상태 머신 | 현재 상태, run\_id | RunRecorder |
| `SceneBuilder` | zone\_graph로 바닥·벽·문·오브젝트 생성 (PC만) | 없음 | ZoneGraph |
| `FireDirector` | 0.1초 틱, 발화, 단서 순서, 문 상태 전달 | IFireEngine 인스턴스 | GameFlow |
| `PlayerExposure` | c 계산·FED 누적·신체 효과 요청 | Exposure | IFireEngine, IPlayerRig, Inventory |
| `Inventory` | 아이템 슬롯, 수건 상태 | 아이템 목록 | — |
| `Interactables` | 문·아이템·경보·수도·엘리베이터·소화기 각각 Intent 처리 | 자기 상태(문 개폐 등) | IInteractionSource |
| `SmokeRenderer`, `FireVfx`, `BodyEffects` | 표현만, 상태 없음 | — | IFireEngine, Exposure |
| `NpcAgent` (복수) | 16.9 행동, 자기 FED | NPC 행동·위치 | NpcBrain, ZoneGraph |
| `NpcBrain` | 캐시 → 서버 → 예비 대사 | 캐시 | ServerClient |
| `DialogueRunner` | dialogue.json 실행, 선택 결과를 Intent·로그로 | 현재 노드 | UI |
| `RunRecorder` | Snapshot 채워 IEventLog에 추가, 1초마다 SAMPLE | IEventLog | IPlayerRig, IFireEngine, Exposure |
| `ServerClient` | REST·WebSocket, 2초 묶음 업로드, 재시도 | 연결 상태, 미전송 큐 | — |
| `UiRoot` | 16.11 화면들 | 화면 상태 | GameFlow 이벤트 |

### 14.5 설정 파일 스키마

모든 파일은 `Assets/FireDrill/Config/`(빌드 시 StreamingAssets로 복사)에 두고, 같은 이름의 JSON Schema를 `contracts/schemas/*.schema.json`에 둔다. 로드 시 스키마에 어긋나면 시작하지 않고 필드 경로를 화면과 로그에 보여준다.

| 파일 | 주인(쓰는 쪽) | 읽는 쪽 | 내용 |
| --- | --- | --- | --- |
| `zone_graph.json` | 사람(평면 기준) | Core, SceneBuilder, 서버 | 3장 + 아래 추가 필드 |
| `scenario.json` | 사람 | Core, GameFlow, NPC | 발화·차단·배치 |
| `fire.json` | AI(튜닝) | Core | 4장 계수 |
| `evaluation.json` | AI 초안, 사람 검토 | Core, 서버 | 7장 구간·배점·근거·문장 틀 |
| `dialogue.json` | AI 초안, 사람 검토 | DialogueRunner | 튜토리얼·NPC 선택지 |
| `npc_cache.json` | `build_npc_cache.py` | NpcBrain | 미리 생성한 반응 |
| `app_config.json` | 시연자 | Bootstrap | 서버 주소·토큰·건물·플랫폼 |

**zone\_graph 추가 필드**: 간선에 `center: [x, z]`(문·통로 중심, 벽 위)와 `wall_axis: "x" | "z"`(문이 놓인 벽 방향), 오브젝트에 `id`와 `kind`(16.10 목록)를 둔다. 구역에 `spawn: [x, y, z]`(선택)을 둔다.

```json
// scenario.json
{"schema_version": 1, "id": "s_demo_fire", "building_id": "demo_3f",
 "player_spawn": {"pos": [6, 0, 13], "yaw": 270},
 "fire": {"origin_zone": "z_storage_303", "fire_at_s": 20},
 "initial_doors": {"e_303_door": true},
 "blocked_edges": [],
 "cues": [{"cue": "sound", "at_s": 0}, {"cue": "smell", "at_s": 5}, {"cue": "alarm", "at_s": 60}],
 "npcs": [{"id": "npc_minji", "name": "민지", "persona": "얼어붙는 후배", "spawn": [8, 0, 15], "initial_action": "freeze"},
          {"id": "npc_junho", "name": "준호", "persona": "엘리베이터로 가려는 동기", "spawn": [-2, 0, 12], "initial_action": "go_elevator"}],
 "timeout_s": 240}
```

`fire_at_s`는 PreFire 진입 후 초, `cues[].at_s`는 발화 후 초다. `smoke` 단서는 시각이 아니라 훈련자가 있는 구역의 D가 0.05를 넘을 때 발생한다.

```json
// fire.json
{"schema_version": 1, "tick_s": 0.1, "source_rate": 0.012, "area_ref_m2": 20,
 "k": {"door_open": 0.15, "door_closed": 0.005, "opening": 0.25, "stair": 0.20},
 "min_layer_y": 0.3, "temp": {"base_c": 20, "per_d_c": 180, "origin_bonus_c": 100}, "hot_door_temp_c": 60,
 "below_layer_factor": 0.2,
 "phases_s": {"initial_end": 30, "growing_end": 60}, "extinguish_needed_s": 5, "extinguish_range_m": 3,
 "fed": {"full_dose_s": 60, "towel": {"none": 1.0, "dry": 0.8, "wet": 0.6}, "stages": [0.30, 0.60, 0.85, 1.00]},
 "render": {"max_opacity": 0.7}}
```

```json
// evaluation.json (항목 1개만 예시)
{"schema_version": 1, "grade_ratio": {"good": 1.0, "normal": 0.5, "poor": 0.0},
 "posture_threshold": 0.7,
 "items": [{"id": "evac_delay", "mvp": true, "weight": 15,
   "thresholds": {"good_max_s": 30, "normal_max_s": 60}, "rule_ref": "FIRE-04",
   "text": {"good": "단서를 보자마자 대피를 시작했습니다.", "improve": "대피 시작까지 {delay_s}초가 걸렸습니다.",
            "guideline": "화재를 알게 되면 즉시 대피합니다."}}],
 "rules": {"FIRE-04": {"source": "국민재난안전포털", "quote": "(원문 확정 후 입력)", "cross_ref": "소방청"}}}
```

항목별 `thresholds` 키는 7장 표의 측정식에 맞춰 항목마다 다르다(예: `low_posture`는 `good_min_ratio`, `normal_min_ratio`). `text`의 `{변수}`는 `evidence`의 키다.

```json
// dialogue.json
{"schema_version": 1, "start": "intro_1",
 "nodes": {
  "intro_1": {"speaker": "교수님(전화)", "text": "301호 교탁에 있는 서류 좀 가져다 줄래?",
              "choices": [{"text": "네, 다녀올게요.", "intent": "accept_errand", "next": null}],
              "on_end": {"quest": "fetch_document"}},
  "npc_minji_fire": {"speaker": "민지", "text": "선배… 이거 무슨 냄새죠?",
              "choices": [{"text": "불이야! 빨리 대피해!", "intent": "warn", "next": null},
                          {"text": "나랑 같이 가자.", "intent": "escort", "next": null}]}}}
```

선택지의 `intent`가 `warn`·`escort`이면 `NPC_INTERACT`, `shout`이면 `SHOUT` 이벤트를 남긴다. 대화창을 닫고 떠나면 선택 없음으로 본다.

```json
// npc_cache.json
{"schema_version": 1, "model": "claude-haiku-5-5", "generated_at": "2026-11-25",
 "entries": {"npc_minji|warn|2|freeze": {"action": "follow_player", "line": "네, 따라갈게요!", "emotion": "scared"}},
 "fallback": {"npc_minji": ["무서워요…"], "npc_junho": ["엘리베이터 타면 빠르잖아!"]}}
```

```json
// app_config.json
{"schema_version": 1, "platform": "pc", "server_url": "http://127.0.0.1:8000", "device_id": "pc-01",
 "device_token": "(시연자 입력)", "building_id": "demo_3f", "scenario_id": "s_demo_fire",
 "offline": false, "language": "ko"}
```

### 14.6 서버 계약

Pydantic 모델을 `server/app/schemas.py` 한 파일에 둔다. C# 쪽 DTO는 같은 필드를 쓴다. 요청·응답에는 모두 `schema_version`이 들어가고, 아래는 그 밖의 필드다.

| 엔드포인트 | 요청 본문 | 응답 본문 |
| --- | --- | --- |
| `GET /health` | — | `{ok: bool, llm: "claude" \| "fake" \| "off", server_time: str}` |
| `POST /sessions` | `{building_id, participant_code: str(≤16), device_id}` | `{session_id: uuid}` |
| `POST /sessions/{sid}/runs` | `{scenario_id, attempt: int}` | `{run_id: uuid}` |
| `POST /runs/{rid}/events` | `{events: LogEvent[] (최대 500)}` | `{accepted: int, last_seq: int}` |
| `POST /runs/{rid}/finish` | `{outcome, evaluation: EvaluationResult}` | `{ok: true}` |
| `POST /runs/{rid}/report` | `{evaluation: EvaluationResult}` | `{report: ReportText, model: str, fallback_used: bool, latency_ms: int}` |
| `POST /npc/react` | `NpcReactRequest`(8장 입력) | `NpcReaction`(8장 출력) + `source: "llm" \| "fallback"` |

`ReportText` = `{summary: str, items: [{id, good: str, improve: str, guideline: str}]}`. `LogEvent`는 6장 공통 필드를 그대로 펼친 형태(`pos`는 `[x,y,z]`)다.

**오류**: HTTP 상태 + `{error: {code, message}}`. code는 `bad_token`(401), `not_found`(404), `invalid`(422), `llm_timeout`(504), `llm_error`(502). 클라이언트는 4xx는 재시도하지 않고, 5xx·네트워크 오류는 1·2·4초 간격으로 3번 재시도한 뒤 오프라인 모드로 전환한다. 이벤트는 큐에 남겨 두었다가 연결이 돌아오면 보낸다(`(run_id, seq)`로 중복 무시).

**WebSocket `/ws/device/{device_id}?token=…`**

| 방향 | 메시지 | 주기 |
| --- | --- | --- |
| 클라이언트 → 서버 | `{type: "status", run_id, state, t, zone, fed, stage}` | 1초 |
| 서버 → 클라이언트 | `{type: "abort", by: "staff"}` | 요원이 누를 때 |
| 서버 → 클라이언트 | `{type: "ping"}` | 2초. 5초 동안 못 받으면 클라이언트는 훈련을 일시정지 |

PC 데모에서 서버 없이 실행하면(`offline: true`) WebSocket 일시정지 규칙은 꺼지고, 비상 중단은 Esc 2초로만 한다.

**LLM 교체**: 서버 환경 변수 `LLM_MODE=claude | fake | off`. `fake`는 고정 응답을 돌려주는 가짜 구현으로, 테스트와 API 키가 없는 개발에 쓴다.

## 15. 통합 순서와 전체 테스트

각 단계는 앞 단계가 통과한 뒤에 시작한다. 아직 없는 모듈은 **가짜 구현**으로 대신해서, 단계마다 항상 실행 가능한 상태를 유지한다. 각 단계는 브랜치 하나, 통과 조건을 만족하면 main에 합친다.

| 단계 | 만드는 것 | 가짜 구현으로 대신하는 것 | 통과 조건 (자동) |
| --- | --- | --- | --- |
| 0. 계약 | 14장 인터페이스·DTO, JSON Schema, Pydantic 모델, 데모 설정 파일 7종, 표준 로그 3종 | 인터페이스 구현 전부 (`NotImplementedException`) | 컴파일 성공, 설정 파일 7종과 표준 로그가 스키마 검증 통과 (C#·Python 양쪽) |
| 1. Core | ZoneGraph → FireEngine → Exposure → EventLog → Evaluator | 없음 | EditMode 테스트: 11장 완료 기준 + 표준 로그 3종의 평가 결과가 기대값과 일치 |
| 2. 서버 | 14.6 전체, `LLM_MODE=fake` | Claude 호출 (fake) | pytest: 모든 엔드포인트, 중복 seq 무시, 타임아웃 시 대체 응답 |
| 3. 걸을 수 있는 데모 | Bootstrap, SceneBuilder, PC 리그·입력, GameFlow, RunRecorder, ServerClient | 화재(농도 0 고정 엔진), UI(디버그 텍스트), NPC 없음 | PlayMode 테스트: 씬 생성 후 모든 구역에 바닥·벽 있음, 자동 플레이가 출구에 도착, 로그 파일과 서버 DB에 같은 이벤트 수 |
| 4. 화재 데모 | FireDirector, SmokeRenderer, FireVfx, PlayerExposure, BodyEffects, Interactables, Inventory | NPC 없음, 리포트는 문장 틀만 | 자동 플레이 2종(모범·실패)의 결말과 항목별 등급이 16.13 기대값과 일치 |
| 5. 리포트 | ReportView, 서버 리포트 연동 | Claude (fake → 사람이 키 넣으면 claude) | 종료 후 10초 안에 리포트 표시, 서버를 꺼도 문장 틀로 표시 |
| 6. NPC | NpcAgent, NpcBrain, 캐시 생성 스크립트 | 캐시는 fake로 생성 후 사람이 Claude로 재생성 | 자동 플레이에서 민지 warn → 따라와 출구 도착, 준호 무시 → 엘리베이터 로비 잔류 |
| 7. 튜토리얼·UI | DialogueRunner, UiRoot 전체, 요원 화면 | 없음 | 자동 플레이가 Boot부터 Report까지 끊김 없이 통과, 비상 중단 1초 안에 Aborted |
| 8. 다듬기 | 에셋 교체, 수치 튜닝 | — | 사람의 시연 체크리스트(16.13) 통과 |

**진행 상황**: 0단계 완료 (PR #1, 브랜치 `stage0/contracts`). Python 계약 테스트 27개, Unity EditMode 9개 통과. CI(`.github/workflows/contracts.yml`)가 PR마다 Python 테스트와 표준 로그 재생성 일치를 확인한다. Unity 테스트는 로컬에서만 돈다.

### 15.1 표준 로그 (golden log)

`contracts/golden/`에 로그와 기대 평가 결과를 쌍으로 둔다. 평가 엔진과 리포트가 바뀔 때마다 이 쌍으로 회귀 테스트를 한다. 기대 결과를 바꿀 때는 사람의 승인을 받는다.

| 파일 | 상황 | 기대 결과 |
| --- | --- | --- |
| `model_run.jsonl` | 수건을 적셔 착용, 민지 동행, 준호에게 경고, 경보, 낮은 자세로 남쪽 계단 탈출 | escaped, MVP 8항목 모두 좋음, 100% |
| `fail_run.jsonl` | 가방 챙기기, 확인 없이 문 열기, NPC 무시, 엘리베이터 앞에서 기다리다 쓰러짐 | failed, 소지품만 보통·나머지 미흡, 6% |
| `na_run.jsonl` | 남쪽 복도에서 단서 직후 계단 문을 확인하고 바로 탈출 | escaped, 코·입 보호·자세는 해당 없음, 알리기·타인 돕기는 미흡, 69% |

표준 로그의 연기 값은 화재 엔진이 아니라 생성기(`gen_golden.py`)의 고정 공식이다. 표준 로그는 평가기만 시험하므로 화재 수치를 바꿔도 다시 만들 필요가 없다.

### 15.2 자동 플레이 (사람 없이 검증)

AI는 화면을 볼 수 없으므로, 게임 흐름은 **스크립트 플레이어**로 검증한다. `ScriptedPlayer`는 `IPlayerRig`와 `IInteractionSource`를 구현하고, `contracts/playscripts/*.json`의 동작 목록을 따라 움직인다.

```json
{"schema_version": 1, "name": "model_play", "time_scale": 4,
 "steps": [{"do": "walk_to", "pos": [6, 0, 5]}, {"do": "intent", "kind": "grab", "target": "o_document"},
           {"do": "wait_for", "event": "CUE_EXPOSED"}, {"do": "crouch", "on": true},
           {"do": "intent", "kind": "use", "target": "o_alarm_corr_m"}, {"do": "walk_to_exit", "exit": "z_exit_stair_b"}],
 "expect": {"outcome": "escaped", "grades": {"alert": "good", "no_elevator": "good"}}}
```

PlayMode 테스트가 플레이 스크립트를 batchmode에서 실행하고, 결말·등급을 `expect`와 비교한다. 화면이 필요한 확인은 각 단계 끝에 **자동 스크린샷**(정해진 위치 5곳, `ScreenCapture`)을 남겨 사람이 보고 판단한다.

### 15.3 AI 실행 환경

| 항목 | 값 |
| --- | --- |
| Unity 경로 | 사람이 설치 후 `tools/unity_path.txt`에 기록 |
| 테스트 실행 | `Unity -batchmode -projectPath unity -runTests -testPlatform EditMode\|PlayMode -testResults ../out/*.xml` |
| 빌드 | `Unity -batchmode -quit -projectPath unity -executeMethod FireDrill.Editor.Build.Pc` |
| 서버 | `cd server && uv run uvicorn app.main:app --host 0.0.0.0 --port 8000`, 테스트 `uv run pytest` |
| 에디터 작업 | 씬·프리팹을 손으로 만들지 않는다. 필요하면 `-executeMethod`로 실행하는 에디터 스크립트로 만든다 |

### 15.4 PC 데모 완성 판정

1. 15장 0\~7단계 자동 테스트 전부 통과
2. 서버를 켜고(실제 Claude) 한 번, 끄고(오프라인) 한 번 끝까지 플레이 가능
3. 사람이 16.13 시연 체크리스트 통과
4. 노트북에서 평균 60fps 이상 (연기가 가장 짙을 때 포함)

## 16. PC 데모 보충

PC 데모를 만들 때 AI가 추측하지 않도록 화면·조작·배치의 기본값을 정한다. 여기 수치도 모두 설정 파일로 빼고, 사람이 플레이해 보며 튜닝한다.

### 16.1 Unity 프로젝트 설정

- 패키지: URP, Input System, UI Toolkit(내장), `com.unity.nuget.newtonsoft-json`, Test Framework. **Meta XR SDK·MRUK·XR Interaction Toolkit은 PC 데모에 넣지 않는다**(Quest 단계에서 추가).
- 어셈블리: `FireDrill.Core`(noEngineReferences), `FireDrill.Runtime`, `FireDrill.Platform.Pc`, `FireDrill.Editor`, `FireDrill.Tests.EditMode`, `FireDrill.Tests.PlayMode`.
- 해상도 1920×1080 창 모드, 목표 60fps, VSync 켬.
- 사람이 넣는 파일: `Assets/FireDrill/Fonts/NotoSansKR-Regular.ttf`, `Assets/FireDrill/Audio/*.wav`(16.6 목록). 없으면 폰트는 에러 화면, 소리는 코드로 만든 비프음으로 대체한다.

### 16.2 씬 자동 생성

씬 파일은 `Bootstrap.unity` 하나뿐이고, 그 안에는 `Bootstrap` 오브젝트 하나만 있다. 이 씬도 에디터 스크립트(`FireDrill.Editor.SceneSetup.CreateBootstrap`)가 만든다. 나머지는 실행 시 `SceneBuilder`가 만든다.

| 요소 | 생성 규칙 |
| --- | --- |
| 바닥·천장 | 구역 다각형을 삼각형으로 나눠 메시 생성. 천장은 `ceiling_h` 높이, 아래쪽 면만 렌더 |
| 벽 | 다각형 변마다 두께 0.1m, 높이 `ceiling_h`. 두 구역이 공유하는 변은 한 번만. 간선 `center`에서 폭 `width`만큼 벽을 비우고, 문 위(2.1m 이상)는 다시 채움 |
| 문 | `door` 간선에 두께 0.05m 문짝. 경첩은 `side_from_handle` 반대쪽 모서리, 열릴 때 90° 회전(0.5초). 손잡이는 `handle` 위치에 작은 막대 |
| 출구 | `exit` 구역 바닥을 초록색으로, 벽에 비상구 표시등(녹색 발광 사각형) |
| 오브젝트 | `kind`별 기본 도형(16.10). 에셋이 들어오면 `Assets/FireDrill/Prefabs/{kind}.prefab`를 우선 사용 |
| 색 | 복도 밝은 회색, 방 베이지, 로비 연한 파랑, 출구 초록. URP Lit, 조명은 구역마다 천장 Point Light 하나 |
| 충돌 | 벽·문·바닥에 Collider. 문은 닫혔을 때만 막는다 |

### 16.3 데모 건물 (`demo_3f`)

남북으로 긴 복도(길이 24m)를 세 구역으로 나눠 연기가 북쪽부터 차오르게 했다. 북쪽 계단(A)은 발화 창고 가까이라 연기가 불리하고, \*\*정답 경로는 남쪽 계단(B)\*\*이다. 엘리베이터 로비는 복도 중간 서쪽에 있다.

| id | type | 다각형 (x범위 × z범위) | 비고 |
| --- | --- | --- | --- |
| `z_corr_s` | corridor | 0\~2 × 0\~8 | 남쪽 복도 |
| `z_corr_m` | corridor | 0\~2 × 8\~16 | 중간 복도 |
| `z_corr_n` | corridor | 0\~2 × 16\~24 | 북쪽 복도 |
| `z_exit_stair_b` | exit | 0\~2 × −3\~0 | **남쪽 계단 (정답)** |
| `z_exit_stair_a` | exit | 0\~2 × 24\~27 | 북쪽 계단 |
| `z_elev_lobby` | elevator\_lobby | −4\~0 × 10\~14 | 엘리베이터 로비, 준호 |
| `z_room_301` | room | 2\~10 × 2\~8 | 강의실, 심부름 서류 |
| `z_lab_302` | room | 2\~10 × 10\~16 | 실습실, 시작 위치, 민지 |
| `z_storage_303` | room | 2\~10 × 18\~22 | 창고, **발화점** |

| 간선 | 연결 | type | center | wall\_axis | 폭 | 초기 |
| --- | --- | --- | --- | --- | --- | --- |
| `e_301_door` | 301 ↔ corr\_s | door | \[2, 5\] | z | 0.9 | 닫힘 |
| `e_302_door` | 302 ↔ corr\_m | door | \[2, 13\] | z | 0.9 | 닫힘 |
| `e_303_door` | 303 ↔ corr\_n | door | \[2, 20\] | z | 0.9 | **열림** |
| `e_corr_sm` | corr\_s ↔ corr\_m | opening | \[1, 8\] | x | 2.0 | — |
| `e_corr_mn` | corr\_m ↔ corr\_n | opening | \[1, 16\] | x | 2.0 | — |
| `e_lobby` | lobby ↔ corr\_m | opening | \[0, 12\] | z | 3.0 | — |
| `e_stair_b` | corr\_s ↔ exit\_b | door | \[1, 0\] | x | 1.2 | 닫힘 |
| `e_stair_a` | corr\_n ↔ exit\_a | door | \[1, 24\] | x | 1.2 | 닫힘 |

방 문의 손잡이는 문 중심에서 z+0.35, 높이 1.0, 양쪽에 둘 다 있다(어느 쪽에서든 확인 가능).

| 오브젝트 id | kind | 위치 \[x, y, z\] |
| --- | --- | --- |
| `o_towel_302` | towel | \[4, 0.8, 11\] |
| `o_faucet_302` | faucet | \[9.7, 1.0, 11\] |
| `o_bag_302` | bag | \[5, 0.8, 15\] |
| `o_phone_302` | phone | \[5.5, 0.8, 15\] |
| `o_document` | document | \[6, 0.9, 3\] (301 교탁) |
| `o_alarm_corr_m` | alarm | \[1.95, 1.2, 9\] |
| `o_ext_corr_s` | extinguisher | \[0.05, 0.5, 6\] |
| `o_ext_corr_n` | extinguisher | \[0.05, 0.5, 17\] |
| `o_elev_button` | elevator\_button | \[−3.95, 1.1, 12\] |

시나리오는 14.5의 `scenario.json` 예시 그대로다(발화 `z_storage_303`, PreFire 20초 후). 튜토리얼에서 301호 서류를 302호로 가져오면 PreFire가 시작된다.

### 16.4 연기 표현

| 요소 | 방식 | 수치 |
| --- | --- | --- |
| 연기층 | 구역마다 박스 메시(다각형 × SmokeLayerY\~천장), URP Unlit Transparent, 회색 | 불투명도 = 0.15 + 0.55×D (최대 0.7) |
| 화면 안개 | 머리가 연기층 안이면 `RenderSettings.fog`(Exponential Squared) | 밀도 = 0.02 + 0.25×c, 색 회갈색 |
| 경계 부드럽게 | 연기층 아래면에 세로 그라데이션(아래 투명) | 두께 0.3m |
| 갱신 | 화재 엔진 틱마다 목표값을 받아 0.5초에 걸쳐 보간 | — |
| 바닥선 | 연기와 관계없이 바닥 가장자리를 연한 발광 선으로 표시(안전 규칙과 동일) | — |

### 16.5 불 표현

발화 구역의 바닥 중심에 파티클(주황 불꽃 + 검은 연기) + 깜빡이는 주황 Point Light. 크기는 화재 단계를 따른다: 초기 지름 0.5m·높이 0.8m, 성장 1.5m·2.0m(천장 닿음), 최성기 방 전체. 소화기로 끄면 3초에 걸쳐 줄어들고 흰 연기만 남는다.

### 16.6 소리

| 파일 | 용도 | 없을 때 대체 |
| --- | --- | --- |
| `fire_crackle.wav` | 타는 소리(발화 구역 3D 사운드) | 저음 노이즈 |
| `alarm.wav` | 건물 경보 | 코드로 만든 1kHz 단속음 |
| `cough.wav` | 1단계 기침 | 자막 "(콜록)" |
| `breath_fast.wav` | 2단계 호흡 | 자막 |
| `door_hot.wav` | 뜨거운 손잡이 | 짧은 치익 소리 |
| `extinguisher.wav` | 소화기 분사 | 화이트 노이즈 |

### 16.7 조작 (PC)

| 동작 | 키 | 수치 |
| --- | --- | --- |
| 이동 | WASD, 마우스 시점 | 걷기 1.4 m/s, 낮은 자세 0.7 m/s, 달리기 없음 |
| 자세 | C 토글 | 머리 1.65m ↔ 0.9m, 전환 0.3초. 선 키는 1.65 고정 |
| 상호작용 대상 선택 | 화면 중앙 레이캐스트 | 거리 1.5m. 대상이 있으면 중앙에 "E: 줍기" 같은 표식 |
| 줍기·사용 | E | 경보 발신기·수도·엘리베이터 버튼도 E |
| 문 열기·닫기 | F | 1.5m 안 |
| 손잡이 확인 | H (0.5초 누르기) | 손잡이 1.0m 안. 뜨거우면 화면 가장자리 붉게 + 소리 |
| 수건 코·입에 | T 토글 | 수건을 가지고 있을 때만 |
| 수건 적시기 | 수도를 보고 E 2초 | 수건을 가지고 있을 때만 |
| 소화기 분사 | 마우스 왼쪽 버튼 유지 | 소화기를 들고, 불을 향해 3m 안. 5초 누적 시 초기 화재 진화 |
| 버리기 | G | 든 물건 내려놓기 |
| 비상 중단 | Esc 2초 누르기 | 즉시 Aborted |

### 16.8 연속 출구 거리

`ExitDistance(pos)` = (현재 구역에서 출구로 가는 최단 경로의 **첫 문 중심까지 직선거리**) + (그 문에서 출구 구역 첫 문까지 문 중심 간 거리 합). 출구 구역 안이면 0. 경로 탐색은 문 중심을 노드로 한 다익스트라이고, 차단 간선은 제외하며 닫힌 문은 지날 수 있는 것으로 본다. 두 출구 중 가까운 쪽을 쓴다. `evac_delay`는 이 값이 5초 동안 연속으로 줄어든(초당 0.3m 이상) 구간의 시작 시각을 쓴다.

### 16.9 NPC 행동

NPC는 캡슐 + 머리 위 이름표(에셋이 오면 교체). 이동은 내비메시 없이 `ZoneGraph.PathTo`가 주는 문 중심 경유점을 따라 직선으로 걷는다(속도 1.2 m/s). 닫힌 문은 NPC가 열고 지나간다(0.5초 멈춤, 문 열림 후 그대로 둠).

| 행동 | 움직임 | 끝나는 조건 |
| --- | --- | --- |
| `evacuate` | 연기가 적은 출구로 이동 (경로 비용 = 거리 × (1 + 3×구역 D)) | 출구 도착 → 구조됨 |
| `follow_player` | 훈련자 뒤 1.5m 유지, 훈련자가 낮은 자세면 NPC도 낮춤 | 훈련자와 10m 이상 멀어지면 `freeze` |
| `freeze` | 제자리, 자막으로 불안한 말 | 훈련자의 warn·escort |
| `ask_help` | 제자리, 훈련자를 부름 | 훈련자의 escort |
| `go_elevator` | 엘리베이터 로비로 가서 버튼 앞에서 대기 (엘리베이터는 오지 않음) | 훈련자의 warn |
| `hide` | 자기 방 구석으로 가서 문을 닫음 | 훈련자의 warn·escort |

- **반응 결정**: 훈련자가 NPC를 보고 E를 누르면 대화창(선택지: "불이야! 빨리 대피해!"=warn, "나랑 같이 가자"=escort)이 열린다. 선택 결과와 연기 등급으로 `NpcBrain`에 물어 다음 행동과 대사를 받는다. 응답을 기다리는 동안 NPC는 훈련자를 바라본다.
- **지나침**: 6장 규칙 그대로(2m 안에 3초, 상호작용 없이 멀어짐) `NPC_INTERACT ignore`를 남기고 행동은 바꾸지 않는다.
- **NPC 피해**: 훈련자와 같은 FED 식(머리 높이 1.6m, 낮추면 0.9m, 수건 없음). FED 1.0이면 쓰러지고(눌힘, 연출 최소) 더 움직이지 않는다.
- **NPC 결과**: run이 끝날 때 `rescued`(출구 도착) / `collapsed` / `remained`를 리포트에 표시한다. 점수에는 `warn_others` 항목만 반영한다.

### 16.10 아이템과 인벤토리

손에 드는 슬롯 1개 + 가방 슬롯 2개. 손에 물건이 있을 때 E로 다른 것을 줍으면 들던 것은 가방으로(가득 차면 바닥에) 간다. 주울 때 걸리는 시간은 모두 1초(이 시간이 `belongings` 측정에 들어감).

| kind | 기본 도형 | 줍기 | essential | 비고 |
| --- | --- | --- | --- | --- |
| `towel` | 흰 판 0.3×0.05×0.2 | O | true | 수도에서 적시면 Wet, T로 착용 |
| `extinguisher` | 빨간 원통 지름 0.15·높이 0.5 | O | true | 15초 분량 |
| `phone` | 검은 판 | O | true | 줍기만, 통화 기능 없음 |
| `document` | 노란 판 | O | false | 튜토리얼 심부름 물건. 화재 후 줍으면 소지품 지연 |
| `bag` | 갈색 상자 | O | false | 화재 후 줍으면 소지품 지연 |
| `alarm` | 빨간 상자 + 버튼 | — | — | E로 누름 → `ALARM_PRESSED`, 건물 경보 즉시 울림 |
| `faucet` | 회색 상자 | — | — | 수건을 들고 E 2초 → `TOWEL_WET` |
| `elevator_button` | 버튼 + 엘리베이터 문 | — | — | E → `ELEVATOR_CALL`, 문은 열리지 않음 |

### 16.11 대화와 UI

UI는 UI Toolkit(UXML·USS 텍스트 파일)으로 만든다. 폰트는 실행 시 `NotoSansKR-Regular.ttf`로 동적 폰트 에셋을 만들어 모든 패널에 적용한다. **화재 중에는 미니맵·방향 표시·체력 게이지를 두지 않는다.**

| 화면 | 언제 | 내용 |
| --- | --- | --- |
| 시작 안내 | Boot 뒤 | 참가자 코드 입력, 훈련 안내(실패 연출·중단 방법), 조작법, 시작 버튼. 서버 연결 상태 표시 |
| 대화창 | 튜토리얼·NPC | 하단 1/3, 화자 이름 + 본문 + 선택지 최대 3개(숫자키 1\~3 또는 클릭). 대화 중에도 게임은 멈추지 않음(화재 중) |
| 자막 | 단서·NPC 대사·신체 효과 | 하단 중앙 한 줄, 3초 표시. 예: "(어디선가 타는 냄새가 난다)" |
| 상호작용 표식 | 대상을 볼 때만 | 화면 중앙 아래 작은 글자 "E: 줍기" |
| 인벤토리 | 항상 | 오른쪽 아래 작은 글자 3줄: 손 / 가방 1 / 가방 2, 수건 상태 |
| 신체 효과 | 단계 변화 | URP Volume: 비네팅·채도·블러. 3단계는 시점 흔들림(PC만) |
| 결말 | Ending | 결과 문구("탈출" / "쓰러졌습니다"), 경과 시간, 마지막 위치의 연기 농도 공개 |
| 리포트 | Report | 상단 결과·총점 / 요약 3\~4줄 / 머리 높이 연기 농도 타임라인(선 그래프, 구역 이동 표시) / 항목표(등급·잘한 점·개선할 점·행동요령) / NPC 결과 / "다시 하기"·"종료" 버튼 |
| 중단 | Aborted | "훈련이 중단되었습니다" + 종료 버튼 |

### 16.12 재시도와 문장 틀

- **재시도**: 새 run\_id, `attempt + 1`. 엔진·문·아이템·NPC·FED를 초기화하고, 훈련자는 302호 시작 위치에서 PreFire로 바로 시작(튜토리얼 생략, 서류는 이미 가지고 있는 상태). 발화는 10초 후.
- **문장 틀**: `evaluation.json`의 항목별 `text`를 쓴다. 요약은 "결과 {outcome}, 총점 {total\_pct}%. 가장 잘한 점: {best}. 가장 먼저 고칠 점: {worst}." 형태. AI가 10개 항목 문장을 초안으로 쓰고 사람이 검토한다.

### 16.13 시연 체크리스트 (사람이 확인)

**모범 플레이** (약 3분): 튜토리얼 → 301호 서류 가져오기 → 302호에서 타는 냄새 → 수건 적셔 착용 → 민지에게 "같이 가자" → 문 손잡이 확인 후 복도로 → 경보 발신기 → 준호에게 "불이야" → 낮은 자세로 남쪽 계단. 기대: 탈출, MVP 8항목 좋음, NPC 2명 구조.

**실패 플레이** (약 2분): 발화 후 가방·서류 챙기기 → 엘리베이터 버튼 → 선 채로 북쪽 복도로. 기대: 쓰러짐 또는 낮은 점수, 리포트가 이유를 설명.

- [ ] 타는 냄새 자막 후 북쪽 복도부터 연기가 차오른다
- [ ] 연기층이 천장부터 내려오고, 낮추면 시야가 확실히 나아진다
- [ ] 뜨거운 문과 그렇지 않은 문을 구분할 수 있다
- [ ] 기침·호흡·시야 변화로 내 상태를 알 수 있다 (게이지 없이)
- [ ] NPC가 선택에 따라 다르게 반응하고, 대사가 어색하지 않다
- [ ] 리포트가 10초 안에 나오고, 점수와 문장이 서로 맞다
- [ ] 서버를 꺼도 끝까지 플레이되고 리포트가 나온다
- [ ] Esc 2초로 언제든 중단된다
- [ ] 두 번째 시도에서 대피 개시 시간이 줄었는지 리포트에서 보인다

### 16.14 PC 데모에서 빼는 것

군중(`CROWD_FOLLOW`), 창문 구조 요청(`WINDOW_SIGNAL`, `blocked_response`), 차단 경로 변형, 음성 대화, Quest 어댑터·앵커 보정. 계약(14장)에는 남겨 두고 구현만 하지 않는다.

## 17. 나중에 바꿀 수 있는 것과 바꾸는 방법

지금 값은 데모를 위한 **적당한 기본값**이다. 대부분은 코드를 고치지 않고 설정 파일만 바꾸면 된다. 바꾼 뒤에는 로컬에서 `uv run --project server python -m pytest server/tests`를 돌리거나 PR을 올려 CI 결과를 보면, 무엇이 깨졌는지 바로 알 수 있다.

| 바꾸고 싶은 것 | 바꾸는 곳 | 바꾼 뒤 자동 확인 | 주의 |
| --- | --- | --- | --- |
| 연기 속도·퍼지는 방식 | `fire.json` `source_rate`, `k`, `edge_k` | 튜닝 목표 테스트(참조 화재 모델) | 목표를 못 맞추면 CI 실패. `ref_fire.py`를 실행하면 구역별 시간표가 나온다 |
| 튜닝 목표 자체 (40/70/150초) | `scenario.json` `tuning_targets` | 위와 같음 | 목표를 바꾸면 `fire.json`도 다시 맞춰야 한다 (AI에게 "목표에 맞게 튜닝"이라고 요청) |
| 발화 위치·시각, 문 이벤트 | `scenario.json` `fire`, `initial_doors`, `door_events` | 상호 참조 테스트, 튜닝 목표 | 다른 발화 위치는 새 시나리오 파일로 만드는 편이 안전 |
| 건물 구조 (실제 건물 확정 시) | `zone_graph.json` | 스키마, 모든 구역에서 출구 도달, 참조 | 구역·문 id가 바뀌면 시나리오·플레이 스크립트·표준 로그도 함께 고쳐야 한다 |
| 판정 구간 (30초·60초, 자세 80% 등)·배점 | `evaluation.json` `thresholds`, `weight` | 표준 로그 기대값 테스트 | 등급이 바뀌면 `gen_golden.py`의 기대값도 고치고 사람이 승인 |
| 평가 문장·근거 원문 | `evaluation.json` `text`, `rules` | 스키마 | 자유롭게 수정 가능 |
| 튜토리얼 대본·NPC 대사 | `dialogue.json`, `scenario.json` `npcs`, `npc_cache.json` | 노드 참조, NPC id 참조 | NPC 성격을 바꾸면 캐시를 다시 생성 |
| 튜토리얼 종료 조건 | `scenario.json` `tutorial` | 대화 노드·오브젝트 참조 | — |
| 신체 효과 단계·수건 효과 | `fire.json` `fed` | 스키마 | 실패까지 시간이 바뀜 |
| 서버 주소·오프라인·보정 건너뛰기 | `app_config.json` | 스키마 | 시연 장소마다 바뀜 |
| 조작 키·이동 속도·UI 배치 | (3·7단계에서 설정 파일로 빼낼 예정) | — | 16장 값이 기본 |
| 데이터 형식(필드 추가·삭제) | `contracts/schemas`, C#·Python 계약 | 양쪽 테스트 | **계약 변경**이다. 이 문서 14장과 저장소를 같은 커밋에서 고친다 |

**원칙**

1. 수치와 콘텐츠는 언제든 바꾼다. 그러나 "데모가 보여주려는 것"(튜닝 목표, 표준 로그 기대값)을 바꾸는 것은 사람이 결정한다.
2. AI가 바꾸는 경우 이유를 커밋 메시지에 적고, 이 문서의 해당 장을 함께 고친다.
3. 아직 명세서에 못 넣은 변경은 저장소 `contracts/README.md`의 "명세서 반영 대기"에 적어 둔다.

## 부록 A. 구현 중 확정된 사항 (2~7단계)

2~7단계를 구현하며 확정한 세부 사항이다. 본문 해당 장보다 이 부록이 우선한다. 저장소 코드(`contracts/`, `Config/runtime.json` 등)가 가장 정확하다.

1. **14.6 요원 화면 보조 엔드포인트**: `GET /staff/devices` → `{schema_version, devices: [{device_id, connected, status: WsStatus|null, status_age_s}]}`. 요원 화면이 1초마다 읽는다. 9·14.6 표에 추가하면 된다.
2. **14.6 토큰**: `?token=` 쿼리도 받는다는 것과 WebSocket 토큰·기기 짝 규칙(위 메모).
3. **14.6 오류**: LLM 실패는 504/502가 아니라 200 + 대체 응답이라는 것(위 메모).
4. **15.3 서버 명령**: 이 PC에서는 `uv run --project server python -m uvicorn app.main:app --app-dir server --host 0.0.0.0 --port 8000 --ws wsproto`, 테스트는 `uv run --project server python -m pytest server/tests`.
5. **14.5 설정 파일 표**: `runtime.json`(주인: AI 튜닝, 읽는 쪽: Runtime·Platform.Pc, 내용: PC 조작·씬 생성·흐름 시간·네트워크 수치) 행 추가, "7종" → "8종". 14장 첫 표의 스키마 "11종" → "12종".
6. **15.3 AI 실행 환경**: PlayMode 명령(`-nographics` 없이 돌리면 스크린샷까지), 프로젝트 설정 `Unity -batchmode -quit -projectPath unity -executeMethod FireDrill.Editor.ProjectSetup.Run`, 빌드 `... -executeMethod FireDrill.Editor.Build.Pc`(→ `unity/Builds/Pc/FireDrill.exe`).
7. **15장 3단계 행**: 가짜 구현에 "튜토리얼(바로 PreFire)"을 더하고, 문 열기·닫기는 3단계에 들어왔다는 것.
8. **15장 4단계 행**: 가짜 구현 "NPC 없음" → "NPC는 서 있는 자리표시(대화 선택·NPC_INTERACT만)". 통과 조건은 `npc_results`를 빼고 확인한다는 것.
9. **16.13 모범 플레이 동선**: 302호 문을 닫고 나오기, 중간 복도에서 낮은 자세로 준호 설득(연기 속에 머무름)을 넣었다는 것.
10. **14.5 `runtime.json` 내용**: 4단계 항목(아이템·효과·자막·소리 대체) 추가.
11. **16.1 글꼴**: "없으면 에러 화면" → "없으면 경고 후 OS 글꼴(맑은 고딕)". 시연 전에는 `NotoSansKR-Regular.ttf`를 넣는다.
12. **16.11 UI 파일 위치**: UXML·USS는 `Assets/FireDrill/UI/Resources/FireDrillUI/`.
13. **14.5 `runtime.json` 내용**: `network.report_timeout_s`, `ui` 섹션 추가.
14. **16.9 NPC 결과**: "탈출 순간 10 m 안에서 따라오던 NPC는 구조됨" 규칙, 문간에서 멈추지 않는 것, NPC가 연 문은 DOOR_OPEN을 남기지 않는 것.
15. **8장 캐시 생성 위치**: `scripts/build_npc_cache.py`(fake/claude, 기존 항목 유지 옵션).
16. **15.2 플레이 스크립트**: `toggle_door`의 `want_open` 필드.
17. **16.13 모범 플레이 동선**: 경보 → 복도에서 낮은 자세로 머묾 → 떠나기 직전 준호에게 알림.
18. **14.5 `runtime.json` 내용**: `npc` 섹션, `network.npc_timeout_s`, `ui.npc_results`.
19. **5장 Calibration**: PC에서는 시작 안내 화면의 "요원 확인 · 훈련 시작" 버튼이 요원 확인이다(참가자 코드 입력 포함).
20. **14.5 `runtime.json` 내용**: `ui.line_s`·`prompts`·`items`·`towel_states`·`endings`.
