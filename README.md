# 업무 책갈피 / WorkBookmark

Windows 탐색기·Excel·Word·PowerPoint·메모장의 작업 위치와 Edge·Chrome 웹페이지를 로컬에 남기는 트레이 도구입니다.

**[0.1.5-preview.1 시험판](https://github.com/prozac0401/BookMark/releases/tag/v0.1.5-preview.1):** 웹 Office의 사이트 루트 사전 접속과 열림 확인 대기, 확장 없이 웹페이지 저장, 사용자별 MSI 설치·로그인 자동 실행을 추가했습니다. [변경 사항과 검증 범위](docs/release-notes-v0.1.5-preview.1.md) · [MSI 설치 안내](docs/windows-installer.ko.md).

**[0.1.4-preview.1 시험판](https://github.com/prozac0401/BookMark/releases/tag/v0.1.4-preview.1):** 데스크톱 Office에서 웹 URL로 연 Excel·Word·PowerPoint의 작업 위치를 저장·재개하도록 확장했습니다. [사용 방법과 사내 AD 자체 검증 항목](docs/office-url-support.ko.md)을 확인하세요. 실제 AD·Office 실기검증은 별도입니다.

**[0.1.3-preview.1 시험판](https://github.com/prozac0401/BookMark/releases/tag/v0.1.3-preview.1):** 메모장 자동 보관. 새 문서·미저장 내용도 파일 선택 없이 본문과 선택 위치를 기록합니다. [변경 및 테스트 분담](docs/notepad-snapshot-and-test-plan.ko.md)을 확인하세요.

**0.1.1 수정판:** Word·PowerPoint의 실제 저장 실패를 수정하고, 메모장 원본 파일 선택·커서 위치 저장을 추가했습니다. [수정 내용과 검증 범위](docs/capture-fixes.ko.md)를 확인하세요.

**현재 상태: 제한된 시험판.** Windows 빌드·규칙/장애 시험 및 일부 실제 연동을 확인했습니다. 다중 탭·Excel 다중 인스턴스 50회, IME, DRM/UNC 등 모든 필수 실기 수용시험을 통과한 정식 v1은 아닙니다. 정확한 상태는 [검증 보고서](docs/validation-report.md)와 [호환성표](docs/compatibility.md)를 확인하세요.

- Ctrl+Alt+B: 탐색기 폴더/단일 선택 항목, Excel 셀, Word 본문 위치, PowerPoint 슬라이드 기록
- 메모장: 새 문서·미저장 내용에서 Ctrl+Alt+B로 본문과 선택 위치 자동 보관. 재개 시 저장 당시 내용의 별도 복원본을 엽니다.
- Edge·Chrome: 확장 없이 Ctrl+Alt+B로 현재 페이지의 제목·URL 저장. 주소 확인이 제한된 화면은 트레이의 ‘웹페이지 URL로 추가…’ 사용. 기존 [브라우저 확장](browser-extension/README.md)은 선택 사항입니다.
- Ctrl+Alt+J → Enter: 최근 저장 위치 재개
- 선택 메모, 검색, 삭제/10초 되돌리기, 접근 실패 항목의 경로 재지정
- 사용자별 로컬 SQLite, 요청마다 별도 STA worker, 외부 전송 없음

[후속 구현 진행·결정 기록](docs/implementation-progress.md) · [한국어 사용 안내](docs/quick-start.ko.md) · [P0 검증](docs/feasibility-report.md) · [구현 결정](docs/decisions.md)

## 실행

`WorkBookmark-0.1.5-win-x64.msi`를 실행하면 현재 사용자에게 설치하고 Windows 로그인 시 자동 실행합니다. 시작 메뉴에서 실행하며, 설정에서 자동 실행을 끌 수 있습니다. 제어판 ‘프로그램 제거’ 또는 Windows ‘설치된 앱’에서 제거할 수 있으며 책갈피 데이터는 유지됩니다. 관리자 권한과 SDK 설치는 필요하지 않습니다.

포터블 사용은 self-contained win-x64 ZIP을 **폴더 전체**로 압축 해제하고 `WorkBookmark.exe`를 실행합니다. 트레이 메뉴에서 종료할 수 있습니다.

## 소스에서 빌드

Windows x64와 .NET SDK **10.0.401**이 필요합니다. 브라우저 확장 자동시험에는 Node.js가 필요하며, PATH에 없으면 build.ps1의 -NodePath로 실행 파일을 지정합니다. 설치되어 있지 않으면 `scripts/bootstrap-sdk.ps1`이 저장소의 `.tools/dotnet`에 공식 ZIP을 내려받고 SHA-512 확인 후 압축 해제합니다. 시스템 SDK를 바꾸지 않습니다.

```powershell
.\scripts\bootstrap-sdk.ps1
.\scripts\build.ps1 -Package -Msi
```

직접 실행 시:

```powershell
$dotnet = ".\.tools\dotnet\dotnet.exe"
& $dotnet restore WorkBookmark.sln --locked-mode --configfile NuGet.Config
& $dotnet build WorkBookmark.sln -c Release --no-restore
& $dotnet run --project tests/WorkBookmark.Core.Tests -c Release --no-build
& $dotnet run --project tests/WorkBookmark.Integration.Tests -c Release --no-build
& $dotnet publish src/WorkBookmark.App -c Release -r win-x64 --self-contained true --no-restore -o artifacts/publish/win-x64
```

배포 MSI·ZIP·Source.zip·검증 JSON·SHA256SUMS.txt는 `artifacts/releases/<빌드시각>/`에 생성됩니다. MSI만 만들 때는 `scripts/build.ps1 -Msi`를 사용합니다. Source.zip은 Git에 커밋한 HEAD에서 생성하므로 ZIP 패키징 전 변경 사항을 커밋해야 합니다. 런타임/패키지는 lock 파일로 고정합니다. 자동시험은 Office 실기시험을 대신하지 않습니다.

## 구조

`Core`: DTO·경로 정책·IPC / `Storage`: SQLite / `Windows`: Shell·Office·실험 PDF / `App`: WinForms UI·수명 관리. `tools/Probes`와 `tools/WindowsChecks`는 합성 자료로 P0를 재현하는 개발용 도구이며 실행 ZIP에 포함하지 않습니다.

[확장 요구명세](docs/extension-requirements.md)와 [확장 검증 결과](docs/extension-validation.md)에 변경 범위·근거·제한을 기록했습니다. 원래 요구사항의 초기 기준은 이력으로 보존했습니다. [시험판 릴리스](https://github.com/prozac0401/BookMark/releases/tag/v0.1.5-preview.1)에서 MSI·실행 ZIP·소스 ZIP·SHA-256을 제공합니다.
