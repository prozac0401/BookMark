# Native Windows adapter diagnostics

This standalone STA console diagnostic is test tooling, not shipped app behavior. It uses the real native object model and product capture adapter, never a mock. The HWND argument must identify a synthetic Excel fixture that you intend to bring to the foreground. The default mode reads metadata only. It does not create/open/save/close a workbook, write cells, or run macros.

Build with the repository SDK:

    .tools\dotnet\dotnet.exe build tools\WindowsChecks\WindowsChecks.csproj

Run with a decimal Excel top-level HWND from the regular probe inventory:

    .tools\dotnet\dotnet.exe tools\WindowsChecks\bin\Debug\net10.0-windows\WindowsChecks.dll 134010

It prints foreground snapshots, native EXCEL7 connection status, Window HWND/IUnknown equality, parent-chain checks and the captured DTO. Only run against synthetic fixtures when keeping or sharing output: a successful DTO includes the document path and sheet name. The handle above is an example from one session, not a reusable identity.

Optional final argument `resume` runs the real resume adapter for the just-captured fixture at `$F$42` and prints phase-only guard/enumeration diagnostics. This mode changes selection and may trigger existing Excel events; it is for synthetic fixtures only. It uses the ordinary single-open resume policy if the captured file closes before reconnection. It never writes cell contents or issues save/close/macro commands.

The initial real run established two issues that compilation could not detect: Excel's accessibility Window wrapper had a distinct COM identity from its collection Window, and LCID 127 failed on ActiveSheet. The corrected capture reads the synthetic saved workbook at 확정자!$D$127 (exact HWND + owning Application checks; LCID 1033). A subsequent enumeration diagnostic identified a hidden XLMAIN control window with no EXCEL7 child. Only when the same PID is fully connected/enumerated can that control window be covered by its Application inventory. The corrected resume then confirmed `$F$42`.

## P0-A active Explorer tabs: bounded method

Create only synthetic fixture folders under testdata/ExplorerP0/A and /B, each with two text files and a child folder. Open two new Explorer windows for those fixture folders. In each new window, manually add a second fixture tab; also include two tabs of A with different selected fixture files. Keep unrelated user folders out of these windows.

There is no documented public Shell API used here to create or activate Windows 11 Explorer tabs. Do not substitute an undocumented WM_COMMAND identifier or claim a multi-window loop proves tab support. For each of 50 steps, choose a fixture tab/window and single selection from a prepared expected-path list, then run the product isolated capture probe after the view is stable. Record the expected path before capture, returned DTO/result, top-level HWND and ActiveViewHwnd. Include two same-folder tabs with different selections. Use no-selection, file, selected-folder and multiple-selection cases. A deliberate transition is accepted only as ContextChanged/AmbiguousTarget or an exact expected snapshot; a different tab's path is always a failure.

Native IShellView.SelectItem or the verified view's ShellFolderView.SelectItem may be used to set synthetic selections after identifying its actual fixture directory. They must never navigate/select in an arbitrary user view or choose the first ShellWindows item sharing a HWND. Do not close user Explorer/Excel windows as cleanup. Fixture windows can be left for the tester to close normally.

Do not report P0-A as passed from the one-window diagnostic or from an always-failing adapter. The acceptance gate requires stable success and zero wrong-tab captures across the prescribed cases.


## Fifty separate-process Excel captures

The bounded synthetic-only runner first rejects every unrelated/unsaved/protected-view workbook, then launches only missing testdata/generated/A or B copies of 같은이름.xlsx with Excel's /x command-line switch. It never uses Workbooks.Open, changes cells, saves/closes books or terminates Excel. Existing fixture windows are reused; ambiguous existing copies cause a failure instead of another launch. Both fixture windows remain open for normal manual closing afterward.

    WindowsChecks.exe series APP_EXE EXCEL_EXE D:\Github_REPOS\BookMark\testdata\generated OUTPUT_JSON 50 $F$42 $F$42

Use shell-safe literal arguments for cell addresses; in PowerShell surround them with single quotes. The two cell arguments are the expected existing A/B positions, not navigation commands. Defaults are the file-generated A=$D$127 and B=$F$42. The example uses A=$F$42 because the preceding P0-C resume deliberately placed A there. The runner refuses to begin if actual initial sheet/cell/Saved state differs from the explicit oracle.

It alternates the verified fixture HWNDs from distinct Excel PIDs, takes the native pre-UI snapshot and sends each capture through the product WorkerClient to the actual app --worker. Each capture has a 3-second deadline. JSON evidence is updated after each attempt with expected/actual identities, result codes, elapsed time, p95, wrong-target/failure counts, launch PIDs and both files' before/after SHA-256. A wrong-target result or the first denied foreground activation stops the series immediately. A denied test-driver activation is a blocked run, with zero product-worker capture attempts for that step. Fifty successful captures establish this separate-process case only; same-process multi-window and the full P0-B gate still need their own evidence.

The first attempted 50-step run produced 50 ForegroundDenied driver results before any worker capture (0 product calls); both fixture hashes were unchanged. Its evidence is retained as Blocked, and the driver was then tightened to stop on the first denial. It is not a passing P0-B result.


## 사람이 전환하는 고정 계획 캡처 세션

기존 native series의 전면 전환이 Windows에 의해 거절되면, 사람이 직접 합성 fixture의 창과 탭을 선택한 다음 시험 전용 단축키를 누르는 방법으로 검증할 수 있습니다. 이 도구는 전면 전환 요청, 키 입력 대행, 문서 열기/셀 이동/저장/종료를 하지 않습니다. 제품의 실제 WorkerClient와 APP_EXE --worker에 캡처 요청만 전달합니다.

    WindowsChecks.exe session APP_EXE testdata\p0-capture-plan.sample.json docs\evidence\manual-capture-NEW.json

1. 먼저 tools/Probes의 fixtures 명령으로 testdata/generated 합성 파일을 준비합니다. 표본 JSON은 여덟 가지 기대 동작을 보여 주는 FocusedChecks 예제입니다. 전체 P0 계획이나 통과 기록이 아닙니다.
2. JSON을 복사해 기대 경로/시트/셀, 안내문, caseId 및 그룹을 시험 전에 확정합니다. Excel의 저장 상태와 활성 셀도 시작 전에 사람이 맞춥니다. 실제 캡처 응답을 기대값으로 복사해 자동 판정하는 기능은 없습니다.
3. 일반 사용자 권한 콘솔에서 위 명령을 실행합니다. 기존 evidence 파일은 덮어쓰지 않으므로 새 출력 파일명을 사용합니다. 시험 도중 APP_EXE와 인접 구현 DLL을 다시 빌드하지 마세요.
4. 콘솔의 다음 단계 안내를 읽고 해당 fixture를 직접 선택한 뒤 Ctrl+Alt+F9를 누릅니다. 기존 제품 단축키 Ctrl+Alt+B/J는 바꾸지 않습니다. 한 요청은 전체 3초이며 진행 중 다른 시험 단축키 입력은 버리고 다음 case로 넘기지 않습니다.
5. 잘못된 대상, 예상하지 않은 거절/timeout, 그룹 불일치는 즉시 종료합니다. 자동 재시도는 없습니다. 취소는 콘솔의 Ctrl+C입니다. 완료/실패/취소 시 시험 단축키를 해제합니다. fixture 창은 그대로 남습니다.

이 세션은 메타데이터 경로를 evidence에 기록하므로 syntheticFixturesOnly:true를 명시해야 합니다. fixtureRoot는 계획 JSON 위치 기준 상대 경로 또는 절대 경로이며, 실제 존재하는 로컬 testdata 폴더 또는 그 하위여야 합니다. UNC와 reparse point를 통한 범위 이탈을 허용하지 않습니다. 기대 경로는 fixtureRoot 안에서 미리 존재해야 합니다. 사용자가 실수로 다른 문서를 선택해 반환된 경로가 루트 밖이면 실제 경로를 기록하지 않고 WrongTargetOutsideFixtureRoot로 중단합니다. 계획의 안내문에도 실제 업무 자료를 넣지 마세요.

계획의 gate는 P0-A, P0-B, FocusedChecks 중 하나입니다. 각 case의 expectedTarget은 제품 CapturedTarget 형식이며 경로는 fixtureRoot 기준입니다. expectedResult가 Captured이면 정확한 expectedTarget이 필수이고, 실패를 기대한다면 expectedTarget을 생략합니다. CaptureTimedOut/AppBusy를 정상 안정 상태의 기대 성공으로 적을 수 없습니다.

| scenario | 기대값 / 용도 |
|---|---|
| explorer-folder | 선택 없는 현재 폴더, Folder |
| explorer-file | 단일 선택 파일, File |
| explorer-selected-folder | 단일 선택 폴더, Folder |
| explorer-same-folder-tabs | 동일 폴더의 탭마다 서로 다른 선택 파일, File |
| explorer-transition | 미리 지정한 정확한 CapturedTarget 또는 ContextChanged/AmbiguousTarget. 안정 상태 50회에서는 제외 |
| explorer-rejection | MultipleSelection 또는 UnsupportedTarget |
| excel-same-process | 같은 프로세스의 서로 다른 창, ExcelCell |
| excel-separate-process | 별도 프로세스의 창, ExcelCell |
| excel-stable | 특정 Excel 위치의 개별 확인, ExcelCell |
| excel-rejection | UnsavedWorkbook/UnsupportedTarget/AmbiguousTarget/EnumerationIncomplete |

windowGroup은 같은 top-level HWND와 PID/프로세스 생성 시각을 뜻하며 서로 다른 그룹은 서로 다른 창이어야 합니다. 탐색기 viewGroup은 같은 실제 ActiveViewHwnd를 뜻하고, Excel processGroup은 같은 PID/생성 시각을 뜻합니다. 탭/창을 닫고 다시 만들거나 Windows 구현이 탭 간 view handle을 재사용하면 의도한 구성을 증명하지 못한 것으로 중단합니다. 그룹 이름만 달리 적어 한 창을 다중 창 시험으로 셀 수 없습니다. 이는 보수적인 검증 도구의 한계이며 제품의 오대상으로 자동 단정하지 않습니다.

P0-A의 CaptureCoverageSatisfied에는 모든 계획 case 통과, 50회 이상의 정확한 안정 캡처, 선택 없음/파일/선택 폴더, 각기 두 개 이상의 실제 view를 가진 두 창, 동일 폴더의 다른 선택을 가진 두 탭이 필요합니다. P0-B에는 안정 캡처 50회 이상과 동명/다른 경로의 같은 프로세스 다중 창 및 별도 프로세스 구성이 모두 필요합니다. 예상대로 거절한 case는 해당 기대값 확인이며 안정 성공 횟수에 더하지 않습니다. FocusedChecks는 횟수와 관계없이 Gate를 확정하지 않습니다.

각 시작/시도/종료 evidence는 같은 디렉터리 임시 파일을 flush한 후 원자 교체합니다. 고정 원문 계획의 SHA-256, 해석한 기대값, APP_EXE 및 있는 경우 WorkBookmark/Core/Windows/Storage DLL의 해시, 스냅샷, 응답, 경과시간, 중단 이유를 남깁니다. workerRequestCount는 WorkerClient 호출 수이고 returnedResponseCount는 응답 수입니다. worker 프로세스가 실제로 시작했음을 모두 뜻하지 않습니다. 진행 중 취소/강제 종료가 있었다면 pendingCaseId와 미완료 상태를 그대로 해석합니다.

종료 코드 0은 해당 계획의 기대값이 모두 일치했다는 뜻입니다. 짧은 계획도 0일 수 있으므로 Gate는 gateEvidenceStatus와 missingCoverage를 확인해야 합니다. 전체 P0의 지연/닫힌 문서 재개, DB 커밋, 실제 UI/IME는 별도이며 overallP0Passed는 항상 false입니다. elapsedMilliseconds도 캡처 worker 진단 시간으로, 제품 캡처→DB 커밋 성능 목표를 대신하지 않습니다.

화면과 Office를 열지 않는 판정 회귀 검증:

    WindowsChecks.exe session-selftest
    WindowsChecks.exe evidence-selftest
    WindowsChecks.exe adapter-checks

session-selftest는 잘못된 경로, 안정 상태 timeout, 예상 거절, 창/view/PID 그룹 불일치, 루트 경계, 불충분한 횟수/시나리오를 확인합니다. 실제 단축키 등록 충돌/해제, 연타 중 다음 case 미소비, Ctrl+C 취소, 사람의 탐색기 탭 전환 및 Excel 다중 창은 별도 실기가 필요합니다.


## Office/PDF/browser extension checks (2026-09-19)

`word-checks` checks numerical Word body coordinates. `pdf-checks` checks parsing plus two private STA DDE servers; its native portion has an eight-second watchdog. It does not certify a Sumatra installation. `BrowserRegistrationOwnershipChecks.ps1` exercises registry ownership with in-memory doubles only.

Generate synthetic Word/PPTX fixtures with `generate-office-fixtures.py` (python-docx and python-pptx). Then use `word-native-checks APP_EXE HWND FIXTURE OUTPUT_JSON` or `powerpoint-checks APP_EXE HWND FIXTURE OUTPUT_JSON`. These commands accept only designated generated fixtures, may navigate their selection, and never save/close/quit Office. Foreground refusal is a failed/blocked attempt, not a passed capture. PowerPoint records whether paneClassDC or the observed mdiClass NativeOM path was used.

Browser native framing tests are in `tests/WorkBookmark.Browser.Tests`; extension API tests are in `browser-extension/tests`. Actual Edge/Chrome installation and end-to-end capture remain separate manual gates. See `docs/extension-validation.md`.


### Read-only Office regression fixtures

Generate additional isolated files without changing existing fixtures or automating document edits:

```powershell
python tools/WindowsChecks/generate-office-fixtures.py --mode read-only
python tools/WindowsChecks/generate-office-fixtures.py --mode reading-restriction
```

The first mode creates `testdata/generated/office-readonly` and applies the filesystem read-only attribute to its DOCX and PPTX. The second creates `office-restricted-readonly` with Word `w:documentProtection` set to read-only in the generated package. It does not remove or change protection on an open document. Open only the designated synthetic fixture, then use its current window handle:

```powershell
WindowsChecks.exe word-native-checks APP_EXE HWND testdata/generated/office-readonly/workbookmark-word-fixture.docx OUTPUT_JSON read-only
WindowsChecks.exe word-native-checks APP_EXE HWND testdata/generated/office-restricted-readonly/workbookmark-word-fixture.docx OUTPUT_JSON reading-restriction
WindowsChecks.exe powerpoint-checks APP_EXE HWND testdata/generated/office-readonly/workbookmark-powerpoint-fixture.pptx OUTPUT_JSON read-only
```

These modes require COM metadata to confirm the requested read-only state before navigation, and compare it again afterward together with the file hash and `Saved` flag. The Word driver records enumeration diagnostics on `EnumerationIncomplete` using validation only. Neither driver changes document content or protection, saves, closes, or terminates Office. Protected View, DRM restrictions, PowerPoint reading/slide-show view, and nonlocal files remain separate unsupported cases; a plain read-only success does not certify them.
