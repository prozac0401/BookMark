# 메모 읽기·편집 중 정렬 집중 검증

2026-10-06 · Windows 11 Pro 10.0.22631 · .NET SDK 10.0.401 · 검사 창 96 DPI.
기준은 v0.2.9 / `e78b1af3c5d3e9b5ecdf00aefa8b40210e8747b8`이다.
두 문제 모두 이 기준의 제품 코드에서 집중 자동검사로 재현했다. 실제 사용자 입력 재현 및 **0.2.9에서 새로 생긴 회귀인지 여부는 확정하지 않았다**.

## 시작 전 확인

- 현재 `main`, `origin/main`, GitHub 원격 main(`git ls-remote`)이 모두 기준 커밋과 일치했다.
- `gh pr list --repo prozac0401/BookMark --state open` 결과는 `[]`였다.
- 시작 시 현재 작업 폴더에 미커밋 변경이 없었다. 기존 `codex/stable-release-20261005`(47aadc0), `codex/backlog-20260927`(6609de7)의 worktree도 깨끗했고 main에 없는 커밋이 없었다. 해당 worktree는 수정하지 않았다.
- README, 초기 구현 명세·수용시험, `docs/decisions.md`의 메모 자동 저장·실패·취소 계약, 관련 스티커·메모·새로고침 검사와 0.2.9 검증 기록을 확인했다.
- 기존 SV15–17은 이미 열린 메모의 **저장** 지연 검사이므로 이번 **읽기** 지연 재현에 재사용하지 않았다.

## 결과

| 항목 | 수정 전 | 수정 후 | 근거 |
|---|---|---|---|
| 메모 읽기 대기 → HideAll → 읽기 완료 | **FAIL**, NR02: Visible=True, Editing=True, Activated=1, 표시 이벤트=1 | **PASS**, NR01–08 8개: Visible=False, Editing=False, Activated=0, 표시 이벤트=0. 숨김 이후 새 요청도 정상 | [수정 전](read-before.log), [수정 후](read-after.log) |
| 하단 편집 확대·위 보정 → 저장 실패 → 일괄 정렬 → Esc/저장 성공 | **FAIL**, SAN04: 정렬 Y=16에 과거 보정값 +88 또는 +132가 더해짐. 최종·저장 Y=104 또는 148 | **PASS**, 4흐름 × 7검사=28개. 정렬·최종·저장 Y=16 일치 | [수정 전](arrange-before.log), [수정 후](arrange-after.log) |
| 최종 Desktop Release 및 참조 제품 프로젝트 빌드 | — | **PASS**, 경고 0·오류 0 | [빌드](final-desktop-build.log) |
| 관련 스티커·메모·표시·정렬·저장·새로고침 회귀 | — | **PASS**, 246개, exit 0 | [관련 검사](related-checks.log) |
| 독립 최종 코드 diff 검토 / `git diff --check` | — | **PASS**, 추가 수정이 필요한 결함 없음 / 공백 오류 없음 | 구현을 맡지 않은 별도 검토자가 제품·시험 diff 검토 |
| 두 흐름의 실제 클릭·키 입력 수용시험 | **NOT RUN** | **NOT RUN** | 아래 Windows UI 제한 참조 |

**이번 관련 검사 246개에는 신규 36개가 포함된다.** 별도 집중 실행 8개·28개를 다시 합산하지 않는다. 과거 [746개 통과](../sticker-alignment-2026-10-06/README.md)는 변경하지 않은 범위의 과거 근거로만 재사용했으며, 이번 실행 결과에 합산하지 않았다. 전체 파이프라인은 재실행하지 않았다.

## 원인과 최소 수정

### 메모 읽기

`BookmarkApplicationContext.EditNoteAsync`는 읽기 완료 후 삭제·갱신 여부를 검사하지만, 대기 중 전체 숨김이 실행됐는지는 검사하지 않았다. 이후 `StickerManager.EditNote`가 `Show/Activate`와 편집을 실행했다.

- `StickerManager.cs`: `HideAll`에서 `HideVersion` 증가.
- `BookmarkApplicationContext.cs`: 요청 당시 숨김 세대를 기록하고 완료 시 달라졌으면 열기를 중단. 숨김 이후 새 요청은 이전 읽기가 대기 중이어도 허용한다. 이전 요청의 `finally`는 새 요청의 중복 열기 방지 상태를 해제하지 않는다.
- `NoteReadVisibilityChecks.cs`: 실제 Context와 임시 SQLite를 사용한다. repository Get의 진입·완료를 `TaskCompletionSource`로 제어해 순서를 고정한다. 시간 제한은 교착 감지에만 쓰며, 임의 지연으로 경쟁 순서를 만들지 않는다. 이전 완료 뒤 새 요청 및 이전 읽기 대기 중 새 요청을 모두 검사한다.

### 편집 중 정렬

`PlacementBounds`는 편집 전 위치와 편집 시작 시 보정된 기준점의 차이를 유지한다. 기존 `ArrangeAsync`가 `Location`만 바꾸면 과거 보정값이 새 정렬 위치에 더해져 저장되고, `RestoreNotePresentation`에서도 그 잘못된 위치로 복원됐다.

- `StickerForm.cs`: `SetArrangedLocation`으로 정렬 위치를 적용한 뒤 편집 복원 위치·기준점을 실제 새 위치로 갱신한다. 원래 크기·펼친 크기·접힘·초안·저장 콜백은 유지한다.
- `StickerManager.cs`: 일괄 정렬의 최종 위치 적용에 이 메서드를 사용한다. 일반 직접 이동의 기존 위치 계산은 유지한다.
- `StickerArrangeNoteChecks.cs`: Grid/펼침/Esc, Horizontal/접힘/Esc, Vertical/펼침/저장, Grid/접힘/저장 4흐름을 실제 Form/Manager와 임시 SQLite로 검사한다. 실패 후 초안·편집 영역 유지, 정렬이 추가 저장을 강제하지 않음, 좌표 저장, 원래 사용자 크기·접힘 복원, Esc 취소 및 Enter 재시도를 확인한다.
- `Program.cs`: 두 집중 실행 옵션 및 기존 관련 검사 묶음에 등록. `BookmarkRefreshChecks`도 관련 검사에 포함해 삭제·재캡처의 늦은 읽기 보호를 확인한다.

## Windows UI 실행과 한계

`ManualUiAcceptance`에 이번 두 경로용 선택 모드를 추가했다. 격리 SQLite, 하단의 사용자 크기 스티커·접힌 스티커, 명시적 읽기 보류/완료 버튼, 기존 저장 실패 주입, 읽기 전용 상태 관측을 제공한다. 제품 UI 이벤트를 reflection으로 대신 실행하지 않는다.

최종 제품 DLL을 사용하는 호스트를 `artifacts/manual-ui/note-regressions-20261006-visible`에서 실행했다. 두 실제 스티커 창은 Visible=True, 96 DPI였으나, computer-use의 `list_windows`와 `list_apps` 모두 **‘BookMark 합성 실기 제어’ 패널만 조작 대상으로 반환**했다. 스티커 제목 클릭·Ctrl+E·편집 Esc/Enter와 트레이 정렬을 실제 입력으로 진행할 수 없어 두 수용시험은 **NOT RUN**이다. [관측 상태](ui-observed.json)에서 CompletedReads=0, 편집=False인 점도 이 범위를 보여 준다. noteAttempts=2는 원본 메모 두 개의 seed 쓰기이며 메모 편집 검증 횟수가 아니다.

제어 패널의 스냅샷 기록·정상 종료 버튼만 실제 입력으로 조작했고 [종료 스냅샷](ui-exit.json)과 해당 프로세스 종료를 확인했다. 최초 Hidden 실행은 패널도 노출되지 않아 소유 시험 프로세스만 정리한 뒤 새 fixture로 재실행했다. 이 기동·관측·종료 결과를 두 문제의 실제 UI 검증으로 대체하지 않는다.

자동검사는 실제 WinForms 객체·메시지 루프·SQLite를 사용하되 메서드 호출/합성 키 처리와 실패 주입으로 제어했다. 사용자 마우스·키 입력, 실제 설치·업데이트, 드래그·Alt 드래그, 혼합 DPI·한글 IME는 이번에 **NOT RUN**이다. EXE 기동·MSI·해시 검사를 해당 흐름의 증거로 사용하지 않았다. 실제 UI 수용 확인과 최초 발생 버전 판정이 남아 있다.

## 재실행

저장소 루트 PowerShell에서:

```powershell
.\.tools\dotnet\dotnet.exe build tests/WorkBookmark.Desktop.Tests -c Release --artifacts-path .artifacts/note-read-race -p:RestoreLockedMode=true
.\.tools\dotnet\dotnet.exe .artifacts/note-read-race/bin/WorkBookmark.Desktop.Tests/release/WorkBookmark.Desktop.Tests.dll --note-read-visibility-only
.\.tools\dotnet\dotnet.exe .artifacts/note-read-race/bin/WorkBookmark.Desktop.Tests/release/WorkBookmark.Desktop.Tests.dll --arrange-note-only
.\.tools\dotnet\dotnet.exe .artifacts/note-read-race/bin/WorkBookmark.Desktop.Tests/release/WorkBookmark.Desktop.Tests.dll --stickers-only
```

각 실행은 같은 데스크톱의 포커스·전역 단축키를 공유하므로 순차 실행한다. `--manual-note-regressions <새 artifacts/manual-ui/하위 경로> <동일 빌드 WorkBookmark.exe>`로 UI 호스트를 실행할 수 있다. 읽기 보류 중 종료할 때는 gate를 먼저 해제하는 시험 패널의 정상 종료를 사용한다.

수정 전 로그는 각 문제의 제품 수정 전에 새 집중검사를 실행해 얻었고 exit 1, 수정 후는 exit 0이다. 개발 중 시험 호스트의 internal 타입 접근 빌드 오류를 수정한 이력은 `.artifacts/arrange-edit/logs/fixture-build-error.log`, `.artifacts/note-read-race/logs/red-build.log`에 보존했다. 해당 빌드 오류를 제품 문제 재현으로 세지 않는다.

이 기록의 최초 검증 완료 시점에는 변경을 로컬 소스·시험·증거에 한정하고 commit·push·PR·병합·버전 변경·배포를 하지 않았다. 이후 사용자가 commit·push·릴리스를 승인했으며, 배포 준비와 범위는 [0.2.10 릴리스 노트](../../release-notes-v0.2.10.md)에 기록한다. 실사용 데이터·설정·권한은 변경하지 않았다.
