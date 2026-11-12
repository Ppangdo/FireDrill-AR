# FireDrill-AR

AI 기반 가상 재난 체험형 시뮬레이션 대피 훈련 플랫폼 (캡스톤디자인, 팀 우리는한다스).

실제 건물 위에 가상 화재·연기·인물을 겹쳐 대피를 체험하고, 행동 로그를 국민행동요령 기준으로 판정해 개인별 평가 리포트를 주는 훈련 플랫폼이다. 2026-12-18까지는 **PC 데모**를 만들고, Quest 3 AR은 기기 확보 후 이어 간다.

| 문서 | 내용 |
| --- | --- |
| [`docs/HANDOFF.md`](docs/HANDOFF.md) | 현재 상태, 다음 할 일, 사람이 할 일 |
| [`AGENTS.md`](AGENTS.md) | AI 에이전트 작업 지침, 명령, 개발 PC 제약 |
| [`docs/SPEC.md`](docs/SPEC.md) | 명세서 (구조·데이터·규칙·수치·완료 기준) |
| [`contracts/README.md`](contracts/README.md) | 모듈 계약과 검증 방법 |

## 구성
- `unity/` — Unity 6000.6.8f1 클라이언트 (Core: 화재·평가 로직, Runtime: 게임, UI, PC 플랫폼)
- `server/` — Python FastAPI 서버 (저장, Claude LLM 중계, 요원 화면)
- `contracts/` — JSON 스키마, 표준 로그, 참조 모델

## 빠르게 실행
```bash
uv run --project server python -m uvicorn app.main:app --app-dir server --host 0.0.0.0 --port 8000 --ws wsproto
```
그다음 Unity 에디터에서 `Assets/FireDrill/Scenes/Bootstrap.unity`를 열고 Play, 또는 `unity/Builds/Pc/FireDrill.exe`(빌드 명령은 `AGENTS.md`).
