# 환경별 검증 상태

> 2026-09-21 0.1.4 시험 빌드: 데스크톱 Office의 HTTP/HTTPS 문서 위치 갈무리·재개를 구현했습니다. 전체 자동검사 333항목 통과이며, AD 인증 서버·실제 웹 Office 문서 실기는 미실행입니다. [변경 및 자체 검증 안내](office-url-support.ko.md).

> 최신 변경(0.1.3): 메모장 새 문서·미저장 본문은 파일 선택 없이 자동 보관하고 복원합니다. 이전의 원본 선택/미저장 거절 설명보다 [새 보관 계약](notepad-snapshot-and-test-plan.ko.md)이 우선합니다.

> 2026-09-19 0.1.1 수정: Word·PowerPoint 실제 저장/복원 각각4개, 메모장 명시적 파일 연결·열기13개, 자동검사244개 통과. 메모장 자동 커서 복원과 실제 설치된 브라우저 UI 저장은 지원/검증 완료로 표시하지 않습니다. 최신 근거는 [수정·검증 기록](capture-fixes.ko.md)을 참고하세요.

> 2026-09-19 확장: Edge·Chrome 제목/URL, Word 본문 위치, PPT 슬라이드 및 실험 PDF 구현은 [확장 요구명세](extension-requirements.md)와 [별도 검증 기록](extension-validation.md)을 참조하세요. 아래 초기 지원표는 당시 기준입니다.

검증일: 2026-09-19. 전체 제품 상태는 **제한된 시험판**입니다. 소스 구현과 실제 환경 지원을 구분합니다.

| 환경 | 실제 상태 | 남은 확인 |
|---|---|---|
| Windows 11 Pro x64, build 22631 (23H2) | 실제 빌드·규칙/장애 시험·Explorer/Excel 일부 실기 수행 | 현재 지원 중인 Windows 빌드에서 전체 재검증 필요 |
| 지원 중인 Windows 11 x64 빌드 | 미실행 | 명세의 정식 출시 필수 환경 |
| Microsoft 365 Excel x64 16.0.20326.20144 | 실제 경로·시트·셀 캡처, 열린/종료 후 문서 셀 이동, 별도 프로세스4회 캡처 확인 | 다중 창·별도 프로세스 50회, 전체 형식·예외 시험 |
| Excel 32비트 | 미실행 | x64 앱의 NativeOM 연결/재개 |
| Excel 미설치 | 미실행 | 탐색기·목록 동작 및 Excel 미설치 안내 |
| 실제 로컬 폴더/한글·공백 경로 | 일부 실기 통과 | 선택 파일/폴더·다중 선택·다중 탭 전체 Gate |
| UNC·네트워크/매핑 드라이브 | 미실행 | 시험 공유 폴더 없음, 연결 끊김 포함 필요 |
| DRM/암호/보호된 보기/상승 권한 대상 | 미실행 | 업무 정책·승인된 합성 fixture 없음. 우회 기능 없음 |
| OneDrive/placeholder/긴 경로 | 미실행 | 실제 연결·파일 접근 지연 시험 필요 |
| xlsx | 합성 fixture 실기 수행 | 나머지 X 계열 예외/다중 창 Gate |
| xlsm/xlsb/xls | 구현됨, 형식별 실기 미실행 | 매크로 없는 fixture 및 이벤트 fixture 분리 필요 |
| Windows 10/ARM64/웹 Excel/가상 데스크톱 이동 | 기본 지원 제외 | 이번 범위 밖 |

시험 머신의 Pro 23H2는 Microsoft 업데이트 지원이 2025-11-11 종료된 빌드 계열입니다. 따라서 이 머신의 성공만으로 명세의 ‘지원 중인 Windows 11’ 조건을 충족했다고 보지 않습니다. OS를 변경하거나 업데이트하지 않았습니다. [Microsoft Lifecycle](https://learn.microsoft.com/en-us/lifecycle/announcements/windows-11-23h2-end-of-updates-home-pro)

정확한 실행 증거와 시험 ID 상태는 [validation-report.md](validation-report.md), 환경 값은 [evidence/environment.json](evidence/environment.json)에 있습니다.

후속 구현에서도 OS build 22631, 설치 Excel 파일 버전 16.0.20326.20144를 재확인했습니다. [현재 환경](evidence/continuation-2026-09-19/environment.json). 새 회귀시험의 성공은 이 표의 미실행 Office/환경 항목을 통과로 바꾸지 않습니다.
