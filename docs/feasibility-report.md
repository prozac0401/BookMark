# P0 기술 검증

실제 Windows에서 .NET 10.0.401로 빌드했습니다. 실기 결과는 합성 fixture를 사용했으며, 일반 규칙시험과 분리합니다.

## P0-A 탐색기 활성 뷰

구현 경로: 단축키 전에 foreground HWND/PID/생성 시각/포커스/활성 뷰 HWND 확보 → ShellWindows의 후보 열거 → IServiceProvider/IShellBrowser.QueryActiveShellView → IShellView.GetWindow → foreground의 보이는 정확한 뷰만 채택 → 해당 뷰에서 Folder/SelectedItems 이중 읽기.

현재 저장소 폴더 캡처와 합성 폴더 Shell 열기 요청을 실제 확인했습니다. 증거: `evidence/p0-initial-window-attempt.txt`, `evidence/p0-folder-open-app-worker.json`.

**Gate 상태: 미실행(부분 실기만 수행).** 동일 폴더·다른 선택을 가진 다중 탭/창 50회는 아직 완료하지 않았습니다. SHELLDLL_DefView 가시성은 Windows 구현에 의존하며, 불명확할 때는 저장을 거절합니다. 공식 Shell에 탭 생성/선택 자동화 API가 없어 검증 없이 비공개 메시지를 사용하지 않았습니다. `tools/WindowsChecks/README.md`에 시험 방법을 남겼습니다.

## P0-B Excel foreground 연결

EXCEL7의 NativeOM 연결, Window/Application/Workbook/Worksheet/ActiveCell 관계와 캡처 전후 스냅샷을 확인합니다. 실제 시험에서 두 결함을 발견해 수정했습니다.

1. NativeOM Window와 Application.ActiveWindow의 COM IUnknown이 달라도 같은 창일 수 있었습니다. 정확한 live HWND와 동일 Application을 함께 검증하도록 수정했습니다.
2. invariant LCID 127의 reflection 호출은 ActiveSheet에서 실패했습니다. 자동화 LCID 1033을 명시했습니다.

수정 후 합성 A/같은이름.xlsx의 `확정자!$D$127` 캡처가 성공했습니다. 열린 정확한 문서를 `$F$42`로 이동한 뒤 다시 캡처해 검증했습니다. 증거: `p0-excel-capture-corrected.json`, `p0-excel-open-workbook-resume-corrected.json`, `p0-excel-resume-verified-corrected.json`.

Excel의 숨겨진 XLMAIN 제어 창은 EXCEL7이 없었습니다. 동일 PID의 실제 Application에 연결하고 그 Application의 모든 Workbooks/ProtectedViewWindows를 확인한 경우에만 이 제어 창을 별도 미연결 인스턴스로 세지 않도록 수정했습니다. 보이는 미연결 창·알 수 없는 프로세스는 계속 거절합니다.

**Gate 상태: 차단 / 전체 미완료.** 별도 Excel 프로세스의 A/B 동명 파일을 실제 UI로 교대 활성화한 4회는 모두 정확히 캡처했습니다. native 실행기의 50회는 ForegroundDenied로 제품 캡처 호출이 0회였으므로 통과로 집계하지 않습니다. 같은 프로세스 여러 창과 요구된 반복 Gate는 남아 있습니다. 증거: evidence/excel-multi-ui-smoke.json, evidence/p0-b-50-separate-process.json.

최종 배포본 정상 권한 캡처는 첫 요청에서 3초 시간초과, 같은 foreground의 수동 후속 요청에서 정확한 A/확정자!$F$42 성공을 관측했습니다. 초기 지연 원인을 확정하지 않았으며 50회 성능 Gate를 통과한 것으로 보지 않습니다. 두 결과를 final-published-capture.json 및 final-published-capture-followup.json에 보존했습니다.

## P0-C 닫힌 파일 재개

Windows Shell을 한 번만 호출하고, 정확한 경로의 Excel 문서가 나타나는지 전체 기한 안에서 관찰하도록 구현했습니다. 새로 여는 동안 불완전 열거는 다시 관찰하며, 추가 열기는 하지 않습니다. 초기 시험에서 파일은 열렸으나 관찰이 일찍 중단되는 문제를 수정했습니다.

실제 Excel fixture를 제목 표시줄 닫기로 정상 종료한 뒤 Excel 창이 없음을 확인했습니다. self-contained App worker가 Shell로 한 번 열고 F42까지 이동한 것을 내부 재검증하여 PositionRestoredFocusPending을 반환했습니다. Windows의 foreground 거절을 셀 이동 성공과 구분했습니다. 원본 fixture SHA-256은 전후 동일했습니다.

**Gate 상태: 통과(해당 합성 xlsx/Office 빌드).** 증거: `evidence/p0-closed-excel-resume.json`, 원문 `p0-closed-excel-resume-response.json`. 전면 전환 성공을 주장하지 않습니다. 일반 UI의 Enter부터 시작한 전체 흐름은 별도 U03 시험입니다.

## P0-D 지연·종료

실제 별도 프로세스의 지연/충돌/늦은 응답을 주입했습니다. ID가 다른 응답과 기한 경과 응답은 폐기하고, 동시에 두 작업을 받지 않으며, timeout은 자기 worker만 종료합니다. worker의 자식으로 띄운 무해한 시험 프로세스가 살아 있음을 확인했습니다. Dispose 동기 정리와 기한 경계 회귀시험을 포함해 12개 IPC 시험이 통과했습니다.

**Gate 상태: 부분 통과.** 프로세스/프로토콜 장애시험은 실제 통과. Excel 편집·모달/응답 지연과 UI 전체 응답성 실기는 별도입니다. 증거: `evidence/ipc-tests.json`.

## 판단

구현을 계속할 기술적 근거는 확보했습니다. 정상 경로의 실제 성공을 일부 확인했으나, P0 전체 Gate와 지원 중인 OS의 필수 시험이 남아 있으므로 정식 v1 완료를 선언하지 않습니다.
