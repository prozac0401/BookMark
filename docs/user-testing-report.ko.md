# 현재 구현 설치 및 검증 결과 — 2026-09-19

현재 소스 기준 **제한된 시험판**입니다. 이번 실행에서 기존 192개 검사와 새 통합검사 15개, 총 **207개 자동검사를 통과**했습니다. 실제 Office·탐색기 탭·한국어 IME·설치된 브라우저의 조작 시험까지 통과했다는 뜻은 아닙니다.

[개별 실사용 시험 M01~M50 및 환경 시험 C01~C08](manual-test-guide.ko.md) · [결과 회신 양식](manual-test-results.ko.md) · [자동검사 원본 요약](evidence/user-testing-2026-09-19/summary.json)

## 이 PC에서 바로 사용

- 설치 폴더: C:\Users\wonse\AppData\Local\Programs\WorkBookmark-Preview-20260919
- 실행 파일: 위 폴더의 **WorkBookmark.exe**. 일반 사용자 권한으로 실행합니다.
- 이번 작업에서 앱을 실행했으며 트레이에 상주합니다. Ctrl+Alt+J로 최근 목록을 엽니다.
- 시험 자료 R: **C:\Users\wonse\AppData\Local\Temp\WorkBookmark-Manual-20260919**. Excel A/B, Word/PPT, 일반 파일, 변경시험용 복사본, 기록01~21 폴더를 미리 준비했습니다.
- 기존 데이터 백업: **C:\Users\wonse\AppData\Local\WorkBookmark-backup-20260919-0214**. 앱 종료 상태에서 DB·설정을 복사하고 DB 파일 해시 일치를 확인한 다음 새 앱을 실행했습니다.
- 데이터 위치: %LOCALAPPDATA%\WorkBookmark. 새 앱도 같은 위치를 사용합니다. EXE 폴더를 옮겨도 별도 DB가 생기는 구조는 아닙니다.
- Windows 로그인 자동실행은 이번 작업에서 변경하지 않았습니다.

기본 저장은 **Ctrl+Alt+B**, 재개는 **Ctrl+Alt+J → Enter**입니다. 브라우저는 별도 확장 버튼 또는 **Ctrl+Shift+Y**를 사용합니다. 종료는 트레이 우클릭 → **종료**입니다.

## 다른 폴더/PC 설치

1. 이번 산출물 **WorkBookmark-0.1.0-win-x64.zip**을 새 로컬 폴더에 전체 압축 해제합니다. Windows x64용이며 .NET SDK 설치는 필요 없습니다.
2. 기존 앱이 있으면 트레이에서 종료하고 기존 %LOCALAPPDATA%\WorkBookmark를 보존합니다. 최초 실행에서 DB 스키마 이관이 진행될 수 있습니다.
3. WorkBookmark.exe를 일반 권한으로 실행합니다. 프로그램 하나만 떼어 옮기지 않습니다.
4. Office 위치 시험은 해당 설치형 Office가 있어야 합니다. Explorer/목록 기능에 Office는 필요하지 않습니다.
5. ZIP의 **testdata/generated**에 합성 시험 파일, **docs**에 이 안내, **browser-extension / browser-host / scripts**에 브라우저 설치물이 들어 있습니다.

패키지는 저장소 **artifacts/releases/user-testing-20260919/**에 작성합니다. 실행 ZIP, Source.zip, SHA256SUMS.txt를 함께 보관하세요. 원격 게시·릴리스 업로드는 하지 않았습니다. 공개 v0.1.0-preview.1을 최신 확장 기능이 포함된 이번 빌드로 혼동하지 마세요.

## Chrome·Edge 설치 상태와 다음 단계

사용자가 **두 브라우저 모두 설치/현재 사용자 등록 진행**을 승인했습니다. 사용자가 두 브라우저에서 확장을 직접 로드하고 같은 실제 ID **dgpnfldabohoijkpmnenglfaopbmhneo**를 전달했습니다. 두 HKCU 등록과 manifest를 적용한 뒤 읽어 호스트 경로·허용 origin·실행 파일 존재를 확인했습니다. 설치된 브라우저의 버튼 저장 결과는 별도 확인 중입니다. 자동화 도구의 Browser Use URL policy가 edge://extensions 접근을 거절했으므로 확장 로드는 사용자가 직접 수행했습니다.

재설치할 때의 수동 확장 로드 순서는 다음과 같습니다. 이 PC에서는 이미 완료했습니다.

1. Chrome 주소창에서 chrome://extensions, Edge 주소창에서 edge://extensions를 엽니다.
2. 개발자 모드를 켜고 **압축해제된 확장 프로그램 로드**를 선택합니다.
3. **C:\Users\wonse\AppData\Local\Programs\WorkBookmark-Preview-20260919\browser-extension**을 선택합니다.
4. 표시된 실제 ID(a~p 32자리)로 아래 등록 명령을 실행합니다. 이번 설치의 두 ID는 위 값입니다.

등록은 실제 ID에 대해서만 아래와 같이 실행합니다. 예시의 ID 자리는 실제 값으로 바꿉니다. 관리자 권한은 필요 없습니다.

~~~powershell
$install = "$env:LOCALAPPDATA\Programs\WorkBookmark-Preview-20260919"
& "$install\scripts\register-browser-host.ps1" -Browser Both -HostExecutable "$install\browser-host\WorkBookmark.BrowserHost.exe" -ChromeExtensionId '<Chrome 실제 ID>' -EdgeExtensionId '<Edge 실제 ID>' -WhatIf

# 위 미리보기가 해당 설치를 가리키는지 확인한 뒤:
& "$install\scripts\register-browser-host.ps1" -Browser Both -HostExecutable "$install\browser-host\WorkBookmark.BrowserHost.exe" -ChromeExtensionId '<Chrome 실제 ID>' -EdgeExtensionId '<Edge 실제 ID>'
~~~

앱 실행 → 일반 https://example.com/?wb=manual-a#part-a 탭 → 확장 버튼 **이 페이지 저장** → 앱 최근 목록 → Enter 순으로 각 브라우저에서 확인합니다. 저장 URL은 Windows 기본 브라우저로 열리며 원래 브라우저/탭·스크롤·로그인 복원은 제공하지 않습니다. query/fragment도 로컬 DB에 그대로 저장됩니다.

연결 해제는 아래 명령으로 해당 설치 소유의 사용자 등록만 제거합니다. 브라우저 확장 제거는 각 브라우저에서 별도로 합니다. 기존 책갈피 DB는 제거하지 않습니다.

~~~powershell
& "$install\scripts\unregister-browser-host.ps1" -Browser Both -WhatIf
& "$install\scripts\unregister-browser-host.ps1" -Browser Both
~~~

## 이번 자동검사 결과

| 검사 | 통과 | 검증 범위 |
|---|---:|---|
| Core / SQLite | 21 | 경로·중복·순서·검색·메모·이관·손상 보존·트랜잭션 rollback |
| IPC / worker | 12 | 프레임·요청 ID·기한·취소·worker 종료·자식 프로세스 보존 |
| Desktop 구성 요소 | 28 | 실제 전역 단축키 등록·충돌, 설정, 합성 IME, 메모 실패, 저장 경계 |
| Explorer 어댑터 | 10 | Shell 파일/폴더/ZIP 판정, 입력 경쟁, PIDL 해제 |
| 50회 시험 결과 판정기 | 13 | 잘못된 증거를 통과로 판정하지 않는지 |
| 수동 캡처 세션 판정기 | 14 | 경로·창·탭 그룹·기대값·시험 횟수 검증 |
| Word 좌표 | 16 | 본문 오프셋·선택 종류·범위 축소 검사 |
| PDF 통신 | 28 | Windows DDE 합성 서버 및 페이지 확인 규칙 |
| BrowserHost | 27 | 실제 호스트 EXE·메시지 계약·잘못된 응답·앱 미실행 |
| 브라우저 확장 | 16 | 브라우저 API 대역으로 활성 탭·URL·권한·성공 응답 검증 |
| 등록 소유권 | 7 | 메모리 레지스트리 대역, 타 등록 덮어쓰기 방지 |
| **새 실제 호스트→앱→SQLite** | **15** | 배포 호스트→실제 서버/UI dispatch→임시 SQLite 커밋·중복·rollback·종료 |
| **합계** | **207** | 모두 종료 코드 0 |

추가로 Release 전체 빌드 **경고 0 / 오류 0**, PowerShell 8개 파일 구문 검사, self-contained 앱 worker의 잘못된 프레임 거절·합성 폴더 검증·script URL 거절을 확인했습니다. 실제 확장 설치·Office 문서·Sumatra는 위 합계에 포함하지 않습니다.

새 통합검사는 tests/WorkBookmark.Desktop.Tests/BrowserSqliteChecks.cs에 추가했고 scripts/build.ps1에서도 실행하도록 연결했습니다. 실제 사용자 DB·Office·브라우저 프로필을 사용하지 않습니다. Desktop와 BrowserHost 계열 검사는 같은 이름의 파이프/단축키를 사용하므로 실제 앱 종료 후 순차 실행합니다.

실행 증거는 **docs/evidence/user-testing-2026-09-19/**에 있습니다. SDK는 10.0.401, OS는 Windows 11 10.0.22631 x64였습니다. 초기 셸 실행 및 NuGet 환경 오류는 하위 프로세스 환경을 보정하여 해결했습니다. 배포 smoke 첫 판정에는 검증 코드의 enum 숫자 오기가 있었고, 소스의 Validated=19로 바로잡아 재실행했습니다. 제품 실패로 집계하지 않습니다.

## 실사용 시험 우선순위

1. M01~M17: 첫 실행, 한 번 저장/재개, 메모, 실제 한글 IME, 중복, 검색, 삭제/되돌리기, 단축키, 재시작.
2. M18~M27: 탐색기 선택별 동작, 다중 창·탭 50회, 지원하지 않는 화면, 파일 종류별 열기/위치 표시, 이동·재지정.
3. M28~M40: Excel 좌표·다중 창/프로세스 50회·닫힌 파일 재개·미저장/숨김/보호·응답 지연.
4. M41~M44: Word 본문 위치, PPT SlideID, 재편집/재정렬/삭제 및 지원 경계.
5. M45~M50: Chrome/Edge 설치와 버튼/단축키, URL 중복·탭 전환·오류·연결 해제.
6. C01~C08: 자동실행/재로그인, PDF 뷰어, 네트워크/DRM/Office 구성, 장시간 사용 등 조건부 시험.

각 ID의 정확한 세부 내용은 [실사용 안내](manual-test-guide.ko.md)를 따릅니다. 번호 범위를 한꺼번에 “통과”로 쓰기보다 실제 수행한 개별 ID와 하위 결과를 보내 주세요.

## 알려진 문제와 지원 한계

- **K01 표시 오류:** src/WorkBookmark.App/UI/RecentForm.cs:96은 빈 검색에도 HasMore이면 “검색 결과 100개”를 표시합니다. 최근 기록 21개 이상일 때 실제 기본 표시 20개와 안내가 어긋납니다. 코드 검토로 확인했으며 이번 검증 작업에서는 제품 동작을 수정하지 않았습니다.
- 설정 설명 일부는 아직 Explorer/Excel만 소개하며 Word/PPT/브라우저 범위를 반영하지 않습니다.
- Word는 본문 문자 오프셋이므로 편집 후 같은 문장을 추적하지 않습니다. PPT는 SlideID로 찾고 삭제된 슬라이드를 대신 고르지 않습니다.
- PDF는 SumatraPDF 3.7+ 기능 응답을 확인하는 **실험 구현**입니다. Adobe/브라우저 PDF 페이지 캡처는 미지원이며 실제 Sumatra 시험은 미실행입니다.
- 자동 IME 메시지 검사는 실제 한국어 조합 키 동작을 대신하지 않습니다. Explorer 탭/Excel 다중 창 안정 캡처 50회도 판정기 자동시험과 별개입니다.

## 남은 Confirm / 사용자 작업

| 항목 | 현재 상태 | 확인이 필요한 이유 |
|---|---|---|
| Chrome·Edge 확장/사용자 등록 | **승인 및 등록 완료**, 실제 브라우저 저장 결과 확인 중 | 사용자가 확장 로드를 수행했고 두 HKCU 등록을 검증함 |
| 시작프로그램 켜기·로그아웃/재부팅 | 미실행 | 로그인 실행 설정과 현재 작업 세션에 영향 |
| SumatraPDF 설치 또는 기본 PDF 연결 변경 | 미실행 | 새 프로그램/파일 연결 설정 변경. 기본 연결 변경은 필수로 전제하지 않음 |
| UNC·DRM·이벤트 매크로·32비트/Office 없는 별도 환경 | 준비 대기 | 시험 대상/접근 가능한 환경을 지정해야 함. 실제 업무 문서를 임의 시험하지 않음 |

메모·삭제/되돌리기·합성 파일 이동 등 안내된 일상 실사용 시험은 별도 재승인 요청 없이 진행할 수 있습니다. 경로 재지정 자체의 **경로 변경 확인 → 적용**은 앱의 정상 사용자 확인 화면입니다.
