# 0.2.9 스티커 토글·자석·일괄 정렬 검증

2026-10-06 · Windows 11 Pro 10.0.22631 x64 · .NET SDK 10.0.401 · Node.js 24.13.1

Release 빌드와 자동검사가 통과했습니다. 이번 결과는 공개 배포 전 로컬 릴리스 준비를 위한 검증이며, 실제 설치·업데이트 시험까지 완료했다는 의미는 아닙니다.

## 자동검사

| 범위 | 통과 수 |
|---|---:|
| Core·Storage | 52 |
| IPC | 17 |
| Desktop | 351 |
| Windows Adapter | 10 |
| Excel evidence | 13 |
| Capture session | 14 |
| Word | 24 |
| Office URL | 103 |
| Browser Adapter | 34 |
| Notepad | 28 |
| PDF | 28 |
| BrowserHost | 27 |
| Browser SQLite | 15 |
| 브라우저 확장 JavaScript | 23 |
| Registration Ownership | 7 |
| **합계** | **746** |

[전체 파이프라인 로그](full-validation.log)의 Desktop 345개를 추가 회귀검사를 포함한 [최종 Desktop 351개 결과](final-desktop-checks.log)로 교체해 집계했습니다. 반복 실행을 중복 합산하지 않았습니다. 전체 빌드와 [최종 Desktop 빌드](final-desktop-build.log)는 각각 경고 0개·오류 0개입니다. 기계 판독용 수치는 [verification-summary.json](verification-summary.json)에 있습니다.

추가 검사는 표시·숨김 토글, 사용자 단축키, 비동기 표시 취소, 숨긴 상태의 캡처, 메모 저장 실패 시 초안 보존, 정렬 취소·실패 시 위치와 저장 기록 유지, 자석 정렬 좌표·화면 배율·음수 좌표·겹침 방지와 일괄 정렬을 다룹니다. `WM_MOVING` 합성 메시지 검사는 실제 마우스 드래그와 구분합니다. Office·브라우저 관련 자동검사도 실제 외부 앱 수용 시험과 구분합니다.

## 실제 전역 단축키 입력

커밋 전 0.2.9 self-contained 검증 빌드와 동일한 `WorkBookmark.dll`을 사용하는 격리된 `ManualUiAcceptance` 앱 컨텍스트에서 사용자 지정 전역 단축키 **Ctrl+Alt+Shift+F16**을 Windows `SendInput`으로 입력했습니다. 사용자의 DB 대신 합성 파일·폴더 책갈피 두 개를 사용했습니다.

- 스티커 **2개 표시 → 모두 숨김 → 2개 표시**를 각 단계의 스냅샷으로 확인했습니다.
- 두 창은 96 DPI이며 다시 표시한 위치·크기·메모와 원본 파일이 유지되었습니다.
- 제어 창의 스냅샷·정상 종료 버튼을 실제 입력으로 실행했고 종료 코드 **0**을 확인했습니다.
- 테스트한 DLL의 SHA-256과 관찰 결과는 [native-toggle.json](native-toggle.json)에 기록했습니다. 최종 Commit 모드 패키지의 빌드 식별자는 별도의 `SourceSnapshot.json`과 `SHA256SUMS.txt`를 기준으로 합니다.

초기 숨김 실행은 해당 검증 프로세스만 정리했습니다. 검증 호스트는 번들 `dotnet`을 명시하여 실행했습니다. 이 결과는 설치된 제품의 `Program.Main` 전체 실행 경로 수용 시험은 아닙니다.

기본 **Ctrl+Alt+J 직접 입력은 미실행**이며 사용자 지정 단축키가 사용하는 같은 설정·토글 경로를 확인한 범위입니다. 컴퓨터 제어 도구가 스티커 ToolWindow를 선택 가능한 창으로 제공하지 않아 **실제 드래그·Alt 드래그·혼합 DPI 모니터 간 이동·한글 IME**는 미검증입니다. 이번 MSI의 실제 설치·업데이트·제거·재부팅 시험도 수행하지 않았습니다.

## 설정 화면

최종 테스트 빌드에서 합성 화면 4개를 렌더링했고, [96 DPI 설정 화면](ui/settings.png)에서 자석 정렬 체크박스, Alt 안내, 보기 단축키의 토글 설명과 하단 버튼을 확인했습니다.

## 재현과 패키지 확인

```powershell
.\scripts\build.ps1 -ArtifactsPath .artifacts/release-0.2.9
.\.tools\dotnet\dotnet.exe build tests/WorkBookmark.Desktop.Tests -c Release --no-restore --artifacts-path .artifacts/release-0.2.9
.\.tools\dotnet\dotnet.exe run --project tests/WorkBookmark.Desktop.Tests -c Release --no-build --artifacts-path .artifacts/release-0.2.9
```

소스 커밋 후 이미 통과한 검사를 반복하지 않고 패키지를 만드는 명령입니다.

```powershell
.\scripts\build.ps1 -SkipTests -Package -Msi -ArtifactsPath .artifacts/release-0.2.9 -SourceMode Commit
```

산출물은 `artifacts/releases/<빌드시각>/`에 MSI, 실행 ZIP, `Source.zip`, `SourceSnapshot.json`, MSI `validation.json`, `SHA256SUMS.txt`의 6개 파일로 생성됩니다. 최종 패키지 검사 결과와 실제 검사 수는 동봉한 `WorkBookmark-0.2.9-win-x64.validation.json`, 커밋과 소스 해시는 `SourceSnapshot.json`, 파일 해시는 `SHA256SUMS.txt`를 기준으로 합니다. MSI 구조 검사·관리자 추출은 실제 설치·업데이트 시험을 대신하지 않습니다. 패키지는 미서명이며 공개 릴리스 업로드는 별도입니다.
