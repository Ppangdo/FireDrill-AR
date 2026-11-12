# 인수인계 (2026-11-12)

다른 AI(ChatGPT/Codex 등)나 새 팀원이 이 저장소만 보고 이어서 작업할 수 있도록 정리한 문서다.
지침은 `AGENTS.md`, 명세서는 `docs/SPEC.md`, 계약은 `contracts/README.md`.

## 한 줄 요약
명세서 15장의 0~7단계를 모두 끝내 main에 병합했다. **PC 데모는 처음부터 끝까지(시작 화면 → 튜토리얼 → 화재 → 결말 → 리포트) 자동 테스트로는 돈다.** 남은 것은 8단계(사람이 직접 플레이하며 다듬기)다. 마감은 2026-12-18.

## 진행 상황
| 단계 | 내용 | PR | 테스트 (완료 시점 누적) |
| --- | --- | --- | --- |
| 0 계약 | JSON 스키마, 데모 설정, 표준 로그 3종, 참조 평가기·화재 모델, CI | #1 | Python 27, EditMode 9 |
| 1 Core | ZoneGraph, FireEngine(참조 모델과 오차 5e-7), Exposure, EventLog, Evaluator | #2 | EditMode 84, Python 28 |
| 2 서버 | REST·DB(SQLite), LLM 중계(가짜/Claude, 대체 응답), WebSocket 중단, 요원 화면 | #3 | Python 91 |
| 3 걸을 수 있는 데모 | Bootstrap, 씬 코드 생성, PC 조작, GameFlow, 로그 기록, 서버 연결, PC 빌드 | #4 | EditMode 99, PlayMode 5 |
| 4 화재 데모 | 실제 화재 엔진, FED·신체 효과, 아이템·문·소화기, 연기·불·소리(임시) | #5 | EditMode 103, PlayMode 9, Python 93 |
| 5 리포트 | 리포트 화면(UI Toolkit), 서버 리포트 10초 제한, 문장 틀 대체 | #6 | EditMode 106, PlayMode 13 |
| 6 NPC | NpcBrain(캐시→서버→예비 대사), NpcAgent 이동·행동, 캐시 생성기 | #7 | PlayMode 17 |
| 7 튜토리얼·UI | 대화, 시작 안내·HUD·결말·중단 화면, Boot→Report 전체 흐름 | #8 | EditMode 106, PlayMode 20, Python 94 |
| **8 다듬기** | **아직 시작 전** | — | — |

각 단계의 자세한 변경은 해당 커밋 메시지(`git log --format=%B <커밋>`)에 있다.

## 다음 할 일: 8단계 (다듬기)
완료 판정은 명세서 15.4: ① 자동 테스트 전부 통과 ② 서버를 켜고 한 번, 끄고 한 번 끝까지 플레이 ③ 16.13 시연 체크리스트 통과 ④ 노트북에서 평균 60fps.

**가장 먼저 할 것: 사람이 직접 플레이해 보기.** 지금까지는 자동 플레이와 스크린샷으로만 확인했다. 새 화면을 손으로 눌러 본 사람은 아직 없다.

1. 서버 실행 (AGENTS.md "서버 실행"), PC 빌드 또는 Unity 에디터에서 Play.
   - 에디터: `Assets/FireDrill/Scenes/Bootstrap.unity`를 열면 Hierarchy에 `Bootstrap` 하나만 있다. "No cameras rendering"은 Play 전이라 정상이다. Game 탭을 16:9로 두고 ▶ → 참가자 코드(예: `T01`) → "요원 확인 · 훈련 시작" → 화면 클릭으로 마우스 잡기.
2. 16.13 모범 플레이·실패 플레이를 해 보고, 체크리스트 9개 항목을 확인한다.
3. 어색한 점은 스크린샷과 함께 기록 → AI가 `runtime.json`·`fire.json` 수치나 코드를 고친다.
4. 에셋 교체(아래 "사람이 할 일"), 60fps 측정 → 필요하면 AI가 최적화.

## 사람만 할 수 있는 일
| 일 | 넣는 곳 | 시점 |
| --- | --- | --- |
| 한글 글꼴 `NotoSansKR-Regular.ttf` (없으면 맑은 고딕으로 대체됨) | `unity/Assets/FireDrill/Fonts/` | 시연 전 |
| 소리 6종 (`fire_crackle`, `alarm`, `cough`, `breath_fast`, `door_hot`, `extinguisher` .wav, CC0) | `unity/Assets/FireDrill/Audio/` | 8단계 |
| 불·연기 이펙트, NPC 캐릭터 모델 (선택) | `unity/Assets/FireDrill/Prefabs/{kind}.prefab` | 8단계 |
| 행동요령 근거 원문 10개 (국민재난안전포털 기준, 소방청 대조) | `Config/evaluation.json`의 `rules.*.quote` | 리포트 시연 전 |
| 판정 기준(30·60초, 자세 80% 등) 지도교수 확인 | `Config/evaluation.json` `thresholds` | 8단계 |
| 튜토리얼 대본·NPC 이름·말투 검토 (현재 민지·준호 임시) | `Config/dialogue.json`, `scenario.json` `npcs` | 8단계 |
| Anthropic API 키 발급 → `server/.env`, 실제 NPC 캐시 생성 후 검토 | `server/.env`, `scripts/build_npc_cache.py --mode claude --overwrite` | 시연 전 |
| Quest 3 확보 여부 (기한 11/24, 없으면 PC 데모만) | — | 결정 필요 |
| 실제 훈련 건물 확정 시 평면 → 구역 그래프 | `Config/zone_graph.json` (+ 시나리오·플레이 스크립트 id) | 12/18 이후 가능 |

바꿀 수 있는 값과 바꾸는 방법은 명세서 17장 표에 있다.

## 알아 둘 것
- **LLM 공급자**: 서버는 Anthropic(Claude) API를 쓴다 — NPC `claude-haiku-5-5`, 리포트 `claude-sonnet-5-5` (`server/app/llm.py`). 다른 공급자로 바꾸려면 이 파일과 `server/prompts/`를 고치고 `LLM_MODE`에 항목을 추가한다(계약은 그대로). 키가 없으면 `fake`로 모든 기능이 돈다.
- **평가는 클라이언트(Unity Core)가 한다.** 서버는 저장·LLM 문장·비상 중단 전달만 한다. LLM은 점수를 바꾸지 않는다.
- **참조 모델**: `contracts/tools/ref_eval.py`, `ref_fire.py`가 C# 구현의 기준이다. 평가 규칙이나 화재 공식을 바꾸면 둘 다 고치고 테스트로 일치를 확인한다.
- **화재 튜닝 목표**: 북쪽 복도 40초·중간 70초에 연기층 1.65 m 아래, 남쪽 150초까지 위 유지. `scenario.json` `tuning_targets`에 있고 CI가 확인한다.
- **개발 PC 제약**: Windows 스마트 앱 컨트롤 (AGENTS.md 참고). Unity를 열 때 `Unity.Properties.SourceGenerator.dll` 차단 알림이 뜰 수 있는데, 지금까지 빌드·테스트에는 영향이 없었다.
- **Unity 버전** 6000.6.8f1은 LTS가 아닐 수 있다. Quest 단계에서 Meta XR SDK 지원 버전을 확인한다.
- **인수인계 시점의 작업 폴더**: 누군가 Unity 에디터를 열면서 `unity/ProjectSettings/`의 일부 파일(GraphicsSettings, QualitySettings, PackageManagerSettings, URPProjectSettings)이 바뀌었고, 이 변경은 커밋하지 않았다. 의도한 변경인지 확인한 뒤 커밋하거나 되돌린다.

## ChatGPT(Codex)로 이어서 작업하는 방법
1. **저장소 연결**: ChatGPT의 Codex에 GitHub 계정을 연결하고 `Ppangdo/FireDrill-AR`(private) 접근을 허용한다. Codex는 `AGENTS.md`를 자동으로 읽는다.
   - 로컬 Codex CLI라면 `C:\dev\FireDrill-AR`에서 실행한다. 개발 PC의 제약은 그대로 적용된다.
   - 클라우드에서 돌리면 Unity가 없으므로 Python 테스트·서버 작업만 가능하다. Unity 작업은 개발 PC에서 한다.
2. **첫 요청 예시**:
   > `AGENTS.md`, `docs/HANDOFF.md`, `docs/SPEC.md`를 읽고 현재 상태를 요약해 줘. 그다음 8단계를 시작하자. 내가 직접 플레이한 피드백은 아래와 같아: …
3. **채팅만 쓰는 경우**: `docs/HANDOFF.md`, `docs/SPEC.md`, `AGENTS.md`를 첨부하고 시작한다. 코드는 필요한 파일만 붙인다.
4. 작업 규칙은 지금과 같다: 단계 브랜치 → 테스트 통과 → PR → main 병합. 계약을 바꾸면 `docs/SPEC.md` 14장도 같이 고친다.

## 결정 기록 위치
- 확정된 결정과 바꾸는 곳: `docs/SPEC.md` 12장("결정됨" 표), 17장
- 구현 중 확정된 세부 사항: `docs/SPEC.md` 부록 A
- 각 단계의 판단 이유: 커밋 메시지
