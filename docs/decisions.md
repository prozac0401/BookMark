# 구현 결정 기록

기준: 저장소의 00/01/02 명세. 기존 문서·사용자 변경을 보존했으며 요구 범위를 임의로 확장하지 않았습니다.

| 결정 | 근거와 사용자 영향 |
|---|---|
| .NET 10.0.401 / WinForms / self-contained win-x64 | 공식 .NET 10 최신 안정 SDK 메타데이터와 SHA-512 확인. 개발 머신 시스템 .NET 8은 변경하지 않고 로컬 SDK 사용. |
| Microsoft.Data.Sqlite 10.0.12 | 초기 10.0.0 복원에서 SQLitePCLRaw 보안 경고 NU1903 확인. 10.0.12와 종속성 lock으로 수정. |
| SQLite DELETE journal + FULL sync | WAL 복사 혼동을 피하는 초기 선택. 쓰기는 트랜잭션, 이전 스키마는 BackupDatabase 후 migration. 실패 시 자동 초기화 없음. |
| 표준 입출력 길이 프레임 | 256 KiB 제한, 프로토콜 버전·요청 ID·전체 기한, 메모 미전송. COM은 worker 안에만 유지. |
| 요청당 STA worker 하나 | COM 지연을 UI에서 격리. 부모는 자체 worker만 Kill(false), 외부 프로세스 트리는 종료하지 않음. 종료 시 동기 정리도 수행. |
| 캡처 전체 3초 / 재개 15초 | 초기 구현은 모든 캡처에 보수적인 3초를 적용. UNC에 5초까지 동적 연장하는 프로토콜은 아직 구현하지 않았으므로 느린 네트워크는 실패할 수 있음. |
| 탐색기 활성 뷰 | ShellWindows→IServiceProvider→IShellBrowser→IShellView의 실제 HWND를 검증. SHELLDLL_DefView 가시성이라는 Windows 빌드 의존 판별을 격리하고 불명확하면 거절. 완전한 원자적 스냅샷은 아님. |
| Excel 창 연결 | EXCEL7→AccessibleObjectFromWindow. 실제 Office에서 경로별 Window IUnknown이 달라짐을 확인하여 살아 있는 HWND와 동일 Application을 함께 비교. 문서·시트·셀 부모 관계는 별도로 검증. |
| Excel COM LCID 1033 | invariant LCID 127 호출이 실제 Office에서 TYPE_E_INVDATAREAD 실패. en-US 자동화 LCID를 명시하며 문서 문자열을 변환하지 않음. |
| 병합 셀 | MergeArea의 맨 왼쪽 위 단일 셀로 캡처·재개를 통일. 실제 병합 fixture의 전체 검증은 별도 Gate. |
| 열린 Excel 파일 식별 | ordinal 경로 우선. 대소문자/파일 ID의 잠재 별칭은 안전하게 확인되지 않으면 EnumerationIncomplete. 영구 DB 키는 합치지 않음. |
| 상승 권한 경계 | worker에서 TokenElevation을 확인. 캡처 대상이 상승 권한/조회 불가이면 거절하며, Excel 열거가 불완전하면 중복 열지 않음. 실기 예외 조합은 미실행. |
| 재개 이탈 감지 | UI와 worker에서 일시적인 새 키/버튼 누름·휠 발생 여부만 감지. 입력 내용은 읽거나 저장하지 않음. 시작 키 해제는 무시. 이미 전달된 외부 호출은 취소 보장 없음. |
| 사용자 시작프로그램 | 사용자 선택 때만 별도 --startup-worker STA helper에서 공식 ShellLink COM 사용. 수제 .lnk가 실제 GetPath 검증에 실패해 폐기. 대상은 앱 자신의 바로가기뿐. |
| 별도 시험 실행기 | 외부 테스트 프레임워크 없이 실제 예외·비교 결과로 실패 exit code를 내는 콘솔 harness. 모의 worker는 tests에만 있고 제품 실행 경로에 없음. |
| 출시 표기 | 필수 실기 Gate를 모두 통과하기 전 UI·문서에 ‘제한된 시험판’을 유지. 컴파일/자동시험과 Office 실기를 구분. |

공식 근거: [QueryActiveShellView](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellbrowser-queryactiveshellview), [AccessibleObjectFromWindow](https://learn.microsoft.com/en-us/windows/win32/api/oleacc/nf-oleacc-accessibleobjectfromwindow), [.NET 10 릴리스 메타데이터](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json), [Microsoft.Data.Sqlite 10.0.12](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12).

## 2026-09-19 후속 구현

각 결정의 당시 상황·문제·근거·사용자 영향·검증 범위는 [후속 진행 기록](implementation-progress.md)에 자세히 남깁니다.

| 결정 | 근거와 사용자 영향 |
|---|---|
| 새 기능보다 P0 검증과 확인된 결함을 우선 | 이미 시험판 기능은 존재합니다. 다른 파일/셀 저장을 막는 정확성이 먼저입니다. |
| 캡처 시작부터 응답 승인까지 새 입력 감시 | 창이 같아도 선택이 달라질 수 있습니다. 새 입력이 있으면 저장하지 않으며, 시작 키 해제는 무시합니다. 확정된 DTO의 DB 저장 중에는 다음 작업을 허용합니다. |
| 선택한 ZIP은 파일시스템 종류로 분류 | Shell의 폴더 탐색 속성과 실제 디렉터리는 다릅니다. ZIP은 파일로 저장하고 위치 표시 정책을 적용합니다. |
| Shell 경로 해석 뒤 사용자 이탈 재확인 | 지연 중 다른 작업을 시작했다면 추가 위치 표시 요청을 보내지 않습니다. 이미 전달한 명령의 취소를 보장하지 않습니다. |
| 수동 단축키 P0 세션 + 사전 고정 기대값 | 시험 도구의 전면 전환 거부 문제를 피하면서 실제 선택한 대상의 정확도를 검증합니다. 시험 응답을 정답으로 사용하지 않습니다. |
| 작은 표본·해시 실패·전체 Gate 구분 | 50회 미만과 별도 프로세스 사례만으로 전체 P0-B를 통과시키지 않습니다. 준비 실패 시간은 캡처 p95가 아닙니다. |
| 3초 캡처 기한 유지 | 첫 캡처 지연의 원인이 아직 밝혀지지 않았습니다. 측정 없이 시간을 늘려 문제를 숨기지 않습니다. |

## 2026-09-22 스티커 수정 후보 0.2.1

사용자의 네 가지 불편과 포스트잇 모드 최상단 표시 요청을 [수정 요구사항·검증 기록](sticker-fixes-v0.2.1.ko.md)에 연결합니다. 공개 0.2.0과 로컬 수정 후보를 구분합니다.

| 결정 | 근거와 사용자 영향 |
|---|---|
| 스티커 모드에서 항상 위에 표시 | 단축키를 눌러야만 스티커를 찾을 수 있다는 피드백과 최상단 표시 요청에 따른 변경입니다. 이전 비고정 배치도 적용하며 고정 토글을 제거합니다. 위치·크기·접기와 명시적인 숨김은 유지하고 DB v5는 변경하지 않습니다. |
| 외부 작업 진행 표시는 선택한 책갈피에 한정 | 기존 단일 worker 작업 제한은 유지하면서 다른 스티커의 버튼 문구·활성 상태가 함께 바뀌는 문제를 수정합니다. 캡처에는 진행 표시할 원본 스티커가 없습니다. |
| 스티커 모드 메모는 같은 창에서 명시적으로 저장 | 별도 화면 구석으로 이동하지 않고 입력하며, 저장 실패 시 초안을 유지합니다. 목록 모드의 기존 메모 창은 유지합니다. 읽기·저장 완료가 지연돼도 더 최신 책갈피 갱신을 덮어쓰지 않게 합니다. |
| 파일 종류 아이콘은 확장자 기반 비동기 Shell 조회 | 실제 업무 파일을 읽지 않습니다. 즉시 기본 아이콘을 표시하고 최대 250ms 기다린 뒤 기본 아이콘을 유지할 수 있습니다. 네이티브 조회는 한 번에 하나, 캐시는 최대 128종이며 오래된 응답과 이미지 자원을 폐기합니다. |

## 2026-09-22 메모 영역 클릭 편집 0.2.2

사용자 요청에 따라 스티커의 별도 메모 추가·편집 링크를 없애고, 메모 본문이나 빈 안내 영역을 왼쪽 한 번 클릭하면 같은 스티커에서 입력하도록 변경했습니다. Ctrl+E는 유지합니다. 우클릭과 스크롤 조작은 편집 요청으로 처리하지 않으며, 저장·취소 후 메모 영역에 포커스를 돌려도 자동으로 다시 편집하지 않습니다. 저장 방식과 DB v5는 변경하지 않습니다. [변경 및 검증 범위](release-notes-v0.2.2.md)를 따릅니다.
