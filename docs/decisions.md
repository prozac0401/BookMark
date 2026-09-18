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
