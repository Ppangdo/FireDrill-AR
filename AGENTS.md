# FireDrill-AR — AI 에이전트 작업 지침

AR 화재 대피 훈련 플랫폼(캡스톤, 팀 우리는한다스). 코드는 AI가 구현하고 사람은 결정·기기 확인·에셋을 맡는다.
이 파일은 도구와 상관없이(ChatGPT/Codex, Claude Code 등) 이 저장소에서 일하는 AI가 먼저 읽는 지침이다.

**처음이면 순서대로 읽는다**: 이 파일 → `docs/HANDOFF.md`(현재 상태·다음 할 일) → `docs/SPEC.md`(명세서) → `contracts/README.md`(계약).

## 단일 기준
- **명세서**: `docs/SPEC.md` (17장 + 부록). 원래 Claude Doc으로 작성했고 2026-11-12에 저장소로 옮겼다. 이제 이 파일을 고친다.
- 14장 "모듈 계약"이 다른 장보다 우선한다. 계약은 `contracts/`, `unity/Assets/FireDrill/Core/Contracts/`, `server/app/schemas.py`에 코드로 고정돼 있고, 둘이 다르면 **저장소 코드가 맞다**.
- 계약을 바꿀 때는 `docs/SPEC.md`와 계약 파일을 같은 커밋에서 고친다. 바로 못 고친 변경은 `contracts/README.md`의 "명세서 반영 대기"에 적는다.
- 명세서와 다르게 구현해야 하면 코드를 쓰기 전에 멈추고 사람에게 묻는다.

## 현재 범위
- **PC 데모만** 만든다 (12/18 목표). Quest 3 미확보, Quest 패키지는 넣지 않는다.
- 진행 단계: 명세서 15장 0~8단계. 0~7단계 완료(main 병합), **8단계(다듬기) 진행 전**. 단계마다 브랜치 `stageN/<이름>`, 통과 조건을 만족하면 PR로 main에 합친다.
- 다빈소프트(연계기업) 연동, OPC UA, 군중, 창문 구조, 음성 대화는 범위 밖.

## 구조
```
contracts/   스키마(JSON Schema), 표준 로그(golden), 플레이 스크립트, 참조 모델(ref_eval.py, ref_fire.py)
docs/        SPEC.md(명세서), HANDOFF.md(인수인계)
scripts/     build_npc_cache.py (NPC 대사 캐시 생성)
unity/       Unity 6000.6.8f1 프로젝트. 코드는 Assets/FireDrill/ (Core, Runtime, Platform, UI, Editor, Tests)
server/      Python FastAPI 서버 (uv로 관리)
out/         로컬 로그·테스트 결과·스크린샷 (git 제외)
```

## 규칙
- 수치는 코드에 쓰지 않는다 → `unity/Assets/FireDrill/Config/*.json`(설정 8종), `server/app/settings.py`.
- `FireDrill.Core`는 UnityEngine을 참조하지 않는다 (noEngineReferences).
- 씬·프리팹을 손으로 만들지 않는다. 필요하면 `-executeMethod`로 실행하는 에디터 스크립트로 만든다.
- LLM은 판정하지 않는다. 점수는 Core의 Evaluator만 만든다. 서버의 LLM 중계는 Anthropic(Claude) API를 쓴다.
- API 키는 `server/.env`의 `ANTHROPIC_API_KEY`에만 둔다 (커밋 금지). 키가 없으면 `LLM_MODE=fake`로 개발한다.
- 평가 규칙이나 표준 로그 기대값을 바꾸는 것은 사람이 승인한다.

## 명령
Unity 경로: `C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor\Unity.exe` (이하 `Unity`)

| 작업 | 명령 (저장소 루트에서) |
| --- | --- |
| 컴파일 확인 | `Unity -batchmode -quit -nographics -projectPath unity -logFile out/compile.log` 후 로그에서 `error CS` 검색 |
| EditMode 테스트 | `Unity -batchmode -nographics -projectPath unity -runTests -testPlatform EditMode -testResults ../out/editmode.xml -logFile out/editmode.log` (`-testResults`는 `unity/` 기준 경로) |
| PlayMode 테스트 | `Unity -batchmode -projectPath unity -runTests -testPlatform PlayMode -testResults ../out/playmode.xml -logFile out/playmode.log` (실제 서버를 띄운다. `-nographics`를 빼야 `out/screenshots/`에 스크린샷이 생긴다) |
| 프로젝트 설정 | `Unity -batchmode -quit -projectPath unity -executeMethod FireDrill.Editor.ProjectSetup.Run` (URP 에셋·Bootstrap 씬 생성) |
| PC 빌드 | `Unity -batchmode -quit -projectPath unity -executeMethod FireDrill.Editor.Build.Pc` → `unity/Builds/Pc/FireDrill.exe` |
| 계약·서버 테스트(Python) | `uv run --project server python -m pytest server/tests` |
| 표준 로그 재생성 | `uv run --project server python contracts/tools/gen_golden.py` |
| 참조 평가기 / 화재 모델 | `uv run --project server python contracts/tools/ref_eval.py` / `ref_fire.py` |
| NPC 캐시 생성 | `uv run --project server python scripts/build_npc_cache.py` (fake, 기존 항목 유지). 키가 있으면 `--mode claude --overwrite` 후 사람이 검토 |
| 서버 실행 | `uv run --project server python -m uvicorn app.main:app --app-dir server --host 0.0.0.0 --port 8000 --ws wsproto` (설정은 `server/.env`, 예시 `server/.env.example`) |
| 요원 화면 | 브라우저로 `http://<노트북 IP>:8000/staff?token=<DEVICE_TOKENS의 토큰>` |

GitHub Actions(`.github/workflows/contracts.yml`)가 PR과 main push마다 표준 로그 재생성 일치 여부와 Python 계약·서버 테스트를 돌린다. Unity 테스트는 라이선스가 필요해서 CI에 없으니 로컬에서 돌린다.

Unity 에디터가 같은 프로젝트를 열고 있으면 batchmode가 실패한다. 사람에게 에디터를 닫아 달라고 한다.

## 개발 PC의 제약 (Windows 스마트 앱 컨트롤)
- 서명 없는 실행 파일·DLL이 차단된다 (오류 4551 "애플리케이션 제어 정책").
- Python은 시스템 Python 3.15(`C:\Users\rnzus\AppData\Local\Python\pythoncore-3.15-64`)로 venv를 만든다. uv가 받는 Python은 차단된다. 처음 만들 때: `uv sync --project server --python <위 경로>\python.exe --python-preference only-system`
- 콘솔 스크립트(`pytest.exe`, `uvicorn.exe` 등)는 막히므로 `python -m <모듈>`로 실행한다.
- 소스 빌드가 필요한 패키지(Cython, `uvicorn[standard]` 등)는 설치가 안 된다. 휠이 있는 패키지만 쓴다.
- 새 PowerShell 창에서 uv가 안 잡히면 `$env:Path`를 사용자·시스템 PATH로 다시 읽는다.
- 다른 PC나 클라우드(CI 포함)에서는 이 제약이 없다. Python 3.12 이상이면 된다.
