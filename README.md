# 업무 책갈피 / WorkBookmark

Windows 탐색기와 설치형 Excel의 현재 위치를 전역 단축키로 남기는 작은 트레이 도구입니다.

**현재 상태: 제한된 시험판.** Windows 빌드·규칙/장애 시험 및 일부 실제 연동을 확인했습니다. 다중 탭·Excel 다중 인스턴스 50회, IME, DRM/UNC 등 모든 필수 실기 수용시험을 통과한 정식 v1은 아닙니다. 정확한 상태는 [검증 보고서](docs/validation-report.md)와 [호환성표](docs/compatibility.md)를 확인하세요.

- Ctrl+Alt+B: 현재 탐색기 폴더/단일 선택 항목 또는 저장된 Excel의 시트·활성 셀 기록
- Ctrl+Alt+J → Enter: 최근 저장 위치 재개
- 선택 메모, 검색, 삭제/10초 되돌리기, 접근 실패 항목의 경로 재지정
- 사용자별 로컬 SQLite, 요청마다 별도 STA worker, 외부 전송 없음

[한국어 사용 안내](docs/quick-start.ko.md) · [P0 검증](docs/feasibility-report.md) · [구현 결정](docs/decisions.md)

## 실행

self-contained win-x64 ZIP을 **폴더 전체**로 압축 해제하고 `WorkBookmark.exe`를 실행합니다. SDK 설치는 필요하지 않습니다. 관리자 권한으로 실행하지 마세요. 트레이 메뉴에서 종료할 수 있습니다.

## 소스에서 빌드

Windows x64와 .NET SDK **10.0.401**이 필요합니다. 설치되어 있지 않으면 `scripts/bootstrap-sdk.ps1`이 저장소의 `.tools/dotnet`에 공식 ZIP을 내려받고 SHA-512 확인 후 압축 해제합니다. 시스템 SDK를 바꾸지 않습니다.

```powershell
.\scripts\bootstrap-sdk.ps1
.\scripts\build.ps1 -Package
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

배포 ZIP·Source.zip·SHA256SUMS.txt는 `artifacts/releases/<빌드시각>/`에 생성됩니다. 런타임/패키지는 lock 파일로 고정합니다. 자동시험은 Office 실기시험을 대신하지 않습니다.

## 구조

`Core`: DTO·경로 정책·IPC / `Storage`: SQLite / `Windows`: Shell·Excel / `App`: WinForms UI·수명 관리. `tools/Probes`와 `tools/WindowsChecks`는 합성 자료로 P0를 재현하는 개발용 도구이며 실행 ZIP에 포함하지 않습니다.

원래 요구사항 문서 3개는 그대로 보존했습니다. [시험판 릴리스](https://github.com/prozac0401/BookMark/releases/tag/v0.1.0-preview.1)에서 실행 ZIP·소스 ZIP·SHA-256을 제공합니다.
