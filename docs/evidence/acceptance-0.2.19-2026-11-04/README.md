# 0.2.19 실사용 검증 — 기능 통과·패키지 검증 중

**실사용 기능 시나리오: 통과. 전체 판정: 최종 패키지 검증 대기.** 현재 후보의 재시작 보존, 실제 문서 열기와 캡처, 검색 유지·외부 앱 전환 시 숨김, 메모 취소, 긴 목록 드래그 스크롤, 기존 폴더로 이동/꺼내기 및 삭제/복원을 확인했다. 최종 패키지 검증과 배포 성공을 뜻하는 문서는 아니다. 기계 판독용 현황은 [results.json](results.json)에 있다.

## 실행 바이너리와 검증 범위

| 구분 | WorkBookmark.dll SHA-256 | 범위 |
| --- | --- | --- |
| 초기 수동 실행 | `6662dbb894349029cc2694dfe7b60962022fec1e3440478ad9b171355dae3378` | 사용자의 일반 드래그, 폴더 펼침/접힘, 한글 메모 저장 및 도구 단축키 시험 |
| 중간 준비 산출물 | `f101695786d1c4cd9113081c0282fba18d0295dafbf5e735a8c433a7647b8ddd` | 포커스 수정 후 목록/설정 123개 검사. 이 해시의 launcher 준비만으로 실사용 통과를 주장하지 않음 |
| 현재 0.2.19 후보 | `28e47e1799072de8c3c0ed02f9c9363a752a348554f64290e9ee22cd96dbfc05` | sourcegen을 포함한 후보, 전체 Desktop 713개 검사 및 아래 C01–C09 실제 조작/DB 확인 |

시험은 격리된 합성 문서와 SQLite를 사용하는 실제 제품 Form에서 진행한다. launcher가 제품 화면을 열고 상태를 읽으며, 사람의 직접 입력과 기존 Computer Use 도구 입력을 구분한다. 별도 C# native 입력 자동화, 추측한 창 ID 바인딩, 시험만을 위한 제품 창 스타일 변경은 사용하지 않았다. 배포 산출물은 가능한 경우 published `Program.Main` smoke와 MSI 구조·관리 추출·파일 해시 검증으로 별도 확인한다. 예비 증거는 수정하지 않고 보존했다.

## 확인한 결과

| ID | 입력/확인 | 결과와 증거 |
| --- | --- | --- |
| U01 | 사람, 초기 바이너리 | 수정키 없이 B를 A 위에 드래그해 새 폴더 생성. A/B의 ParentId 일치와 접힌 자식 숨김 확인. [기록](user-step01-group.json) |
| U02 | 사람, 초기 바이너리 | 폴더 펼침/접힘과 C 위치 복구 정상 응답. C 화면 위치 `(80,324)` 동안 저장 위치 `(80,280)` 유지, 접힘 후 `(80,280)` 복구. [전이](folder-toggle-transitions.json) |
| U03 | 사람, 초기 바이너리 | 한글 입력 후 다른 창 클릭으로 `실사용 검증 한글 메모 123` 저장, 편집기 닫힘 확인. [기록](user-step03-note.json) |
| T01 | Computer Use, 초기 바이너리 | `Ctrl+Alt+Shift+F16`으로 숨김/표시, 관계와 메모 유지. [숨김](tool-hotkey-hidden.json), [표시](tool-hotkey-shown.json) |
| C01 | 현재 후보 재시작 + DB 관찰 | A/B 관계, A의 한글 메모, C의 독립 관계 보존. 새 관찰기는 폴더도 포함한다. [재시작 전](pre-candidate-restart.json), [재시작 후](candidate-restart.json) |
| C02 | Computer Use, 현재 후보 | C의 실제 재개 버튼으로 합성 문서를 Notepad에서 열고 `OpenRequested` 기록 확인. 문서의 위치 복원 성공을 뜻하지 않는다. [DB](candidate-real-capture.json), [로그](candidate-diagnostic.log) |
| C03 | Computer Use, 현재 후보 | Notepad가 활성인 상태에서 `Ctrl+Alt+Shift+F15`로 실제 캡처. `Captured` → `CaptureCommitted`, 정확한 합성 본문을 가진 `NotepadSnapshot` DB 기록 확인. [DB](candidate-real-capture.json), [로그](candidate-diagnostic.log) |
| C04 | 사람, 현재 후보 | 검색 후 같은 앱 제어창을 클릭해도 검색 유지 정상 응답. `실사용` 검색과 A/상위 폴더 결과를 관찰했다. [전이](user-step04-transitions.json), [최종 상태](user-step04-after.json) |
| C05 | 사람, 현재 후보 | A 메모 편집 후 Esc 취소 정상 응답. 변경 초안과 편집 종료를 관찰했고 DB의 `실사용 검증 한글 메모 123`이 유지됐다. [전이](user-step04-transitions.json), [최종 상태](user-step04-after.json) |
| C06 | Computer Use, 현재 후보 | Sky로 Edge 활성화 성공 후 최근 목록이 숨겨짐. 전환 전 `Visible=true`, 전환 후 `false` 확인. [전](candidate-focus-before.json), [후](candidate-focus-after.json) |
| C07 | 사람, 현재 후보 | 30개 합성 항목 목록에서 드래그 중 아래/위 가장자리 스크롤 정상 응답. `topIndex` 0→25와 25→0 이동을 관찰했고, 30/01 항목이 새 폴더 `2f1e78f5-2302-4e64-b4fd-b3735dbfe4cd`의 자식으로 함께 저장됐다. [전이](user-step05-transitions.json), [최종 상태](user-step05-after.json) |
| C08 | 사람, 현재 후보 | 30 항목 삭제·복원 정상 응답. 삭제 상태 7회 관찰 후 복원 시 `DeletedAtUtc=null`, 기존 폴더 ParentId와 `스크롤 항목 30` 메모 보존. [전이](user-step07-transitions.json), [최종 상태](user-step07-after.json) |
| C09 | 사람, 현재 후보 | 29 항목을 기존 폴더에 넣고 최상위로 꺼내기 정상 응답. 중간 ParentId가 기존 폴더 ID, 최종 ParentId는 null이며 메모 유지. [전이](user-step06-transitions.json), [최종 상태](user-step06-after.json) |

5단계 상태 관찰 주기는 500ms다. 실제 양방향 스크롤과 드롭 결과를 확인했으며, 제품 타이머의 정확한 300/120ms 간격을 native 입력으로 계측했다는 뜻은 아니다. C06의 전후 관찰은 13:07:46/13:08:35 UTC이며 숨김 지연시간 측정은 아니다. Edge 활성화 뒤 브라우저 상태 조회는 URL 정책으로 중단됐지만 활성화 성공과 제품 목록 숨김은 별도로 확인했다.

시험 종료 후 tree/list 세션의 최종 `stage=exit` 상태를 보존했다. 목록 세션은 13:08:47.700 UTC 종료 기록에 제품 창 0개를 확인했다. [tree 종료](candidate-tree-exit.json), [list 종료](candidate-list-exit.json).

현재 후보 진단 로그의 단일 관찰값은 재개 `OpenRequested` 3,581ms, 캡처 `Captured` 1,216ms다. 위치 복원 보장이나 일반 성능 보장으로 확대하지 않는다. 메모 취소를 위한 사람의 Esc 입력으로 Computer Use 호출이 중단된 뒤 다음 호출이 정상 복구된 것은 제품 실패로 분류하지 않았다.

초기 바이너리에서 도구가 C를 기존 폴더로 옮기려 한 시도는 관계·위치 변화가 없었다. 이를 드래그 통과나 제품 결함으로 확정하지 않았다. [예비 판정](preliminary-manual-results.json)의 T03을 그대로 보존한다. 초기 수동 성공을 현재 후보에서 동일 동작을 다시 수행한 것으로 바꾸어 기록하지 않는다.

## 자동 회귀와 한계

- 현재 후보의 Desktop 검사 **713/713**, 종료 코드 0, 최종 RESULT 뒤 예외 없음. [로그](../real-use-2026-11-04/shutdown-fixture-fix-full.log). 새 FL26–31은 같은 앱 포커스 유지·외부 전환·메뉴/드래그 지연을 검사하는 **컴포넌트 검사**다.
- sourcegen 격리 빌드의 Core **86/86**, IPC **17/17** 통과. [Core](../real-use-2026-11-04/sourcegen-core-tests.log), [IPC](../real-use-2026-11-04/sourcegen-ipc.log).
- 실제 worker의 합성 Unsupported 요청 반대 순서 비교는 sourcegen/reflection 각 **6/6**. 앞선 sourcegen 최초 요청 시간초과도 보존하므로 합산은 **11/12**다. cold-start 완전 해결이나 일반 성능 보장으로 해석하지 않는다. [조사](../real-use-2026-11-04/sourcegen-investigation-summary.json).
- 이전 전체 실행은 713 assertion 뒤 종료 continuation 예외가 있었다. browser commit 전용 fixture에서 불필요한 시작 마이그레이션을 제외한 뒤 재실행이 정상 종료했다. [수정 전](../real-use-2026-11-04/focus-fix-full-repro.log), [수정 후](../real-use-2026-11-04/shutdown-fixture-fix-full.log). 제품의 초기화 도중 즉시 종료 안전성을 별도로 입증한 것은 아니다.
- 최초 시도의 `CreateGraphics` 예외는 후속 전체 실행들에서 SV15 통과로 재현되지 않았다. [최초 로그](../real-use-2026-11-04/focus-fix-list-settings.log)를 보존한다.

## 최종 패키지 검증 대기

- 최종 패키지 검증: 가능한 경우 published `Program.Main` smoke, MSI 구조·관리 추출·추출된 모든 파일의 해시 대조, 배포 자산 6개 업로드 해시 확인.

`sourceUnchanged: true`는 준비된 합성 원본 파일의 해시 보존을 뜻한다. 제품 소스 전체가 변경되지 않았다는 뜻은 아니다. 실제 한글 조합 입력의 초기 사람 확인과 자동 주입·컴포넌트 IME 검사를 서로 대체하지 않는다.

실제 사용자 설치·업그레이드·제거 전주기는 이번 변경의 실사용 게이트에 추가하지 않는다. 해당 전주기를 수행하지 않았다는 점은 검증 범위의 제한으로 기록한다.
