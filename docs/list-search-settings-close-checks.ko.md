# 목록 유지·설정 닫기 검증 — 0.2.14-test.2

2026-10-14 Windows 11 / .NET SDK 10.0.401에서 실행한 이번 변경의 결과다. 이전 0.2.14-test.1이나 다른 릴리스 결과를 통과 근거로 재사용하지 않았다. 정식 안정판이 아닌 포터블 prerelease다.

## 기존 작업 보존

시작 시 local main/origin/main은 `593e5ae4815cba0d2465928e6320d4b8643e7c6f`, 미커밋 변경은 없었다. 최신 태그·정식 릴리스는 v0.2.13이었다. 원격 `fix/list-search-and-settings-close`의 `48696b2`를 부모로 하는 `fix/list-settings-prerelease`에서 작업했다. 기존 브랜치·다른 worktree·main은 변경하지 않았다.

기존 커밋의 설정 Close() 수정은 재사용했다. 검색 중 접기/펴기 허용과 모든 Deactivate 숨김 제거는 요청한 기존 정책과 맞지 않아 후속 커밋에서 복원했다. 검색은 일치 항목의 상위 경로를 임시로 펼치고, 검색 중 펼침 조작은 저장하지 않으며, 검색 해제 시 저장된 폴더 상태로 돌아간다. DB 형식과 목록/스티커의 폴더 상태 공유는 변경하지 않았다.

## 재현과 수정

- 원본 모델리스 SettingsForm의 닫기 버튼 검사 SET09 FAIL: DialogResult.Cancel만으로 종료되지 않았다. 명시적 Close()로 닫기와 Esc가 미적용 입력을 폐기한다.
- 원본의 실제 WinForms/Win32 구성요소 검사에서 스티커 Show 후 활성 창과 키보드 포커스가 스티커로 옮겨가고 RecentForm.Deactivate가 목록을 숨겼다. SF01–05 FAIL, 이미 비활성화된 스티커의 재표시 SF06 PASS였다. 같은 UI 스레드의 창 전환 재현이며, 실제 마우스 재현으로 주장하지 않는다.
- Enabled 임시 해제는 재표시에 효과가 있었지만 최초 표시 SF01은 FAIL이었다. 호출 스택은 Form.CreateHandle → TopMost setter → SetWindowPos의 추가 활성화 경로를 확인했다. 후보 패치를 그대로 배포하지 않았다.
- 최종 코드는 숨김→표시 동안 현재 UI 스레드에서 해당 스티커/자식의 활성화·포커스 전환만 잠깐 차단한다. scope 종료 시 해제하고 이후 명시적 편집을 허용한다. Enabled·영구 창 스타일을 변경하거나 Activate로 초점을 되찾지 않는다.
- 목록은 TopMost로 같은 창 순서 그룹에 표시한다. 이전 목록 HWND가 표시 후에도 실제 전경일 때만 SWP_NOACTIVATE로 새 스티커를 목록 뒤에 둔다. 다른 창으로 전환할 때의 기존 목록 숨김은 유지한다.

근거: [WinForms Form 소스](https://github.com/dotnet/winforms/blob/v10.0.0/src/System.Windows.Forms/System/Windows/Forms/Form.cs), [Win32 CBTProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/cbtproc).

## 이번 실행 결과

[실행 로그 폴더](evidence/list-settings-2026-10-14/)

| 범위 | 결과 | 증거/제한 |
|---|---|---|
| 원본 설정 닫기 | FAIL (문제 재현) | baseline-settings-focused.log, SET09 |
| 원본 표시 포커스 | FAIL (문제 재현) | baseline-focus-component.log, SF01–05 |
| 최종 Release 빌드 | PASS | 경고 0, 오류 0 |
| 설정 닫기·Esc·적용/실패/재시도 | PASS 24 | 실제 모델리스 Form, 임시 저장 파일과 콜백 횟수 비교 |
| 목록 폴더·검색 해제 | PASS 24 | 기존 검색 정책, 다른 동일 프로세스 창 활성화 시 목록 숨김 |
| 폴더 스티커 | PASS 37 | 편집·이름 변경·저장 실패 경로 포함 |
| SQLite/목록/스티커 통합 | PASS 33 | 닫기/열기 3회, 목록 표시·검색 포커스·Deactivate 0회 |
| 표시 포커스·표시 후 편집 | PASS 8 | 첫 표시/재표시, 다른 동일 스레드 창, disabled/busy, 명시적 편집 |
| 실제 전경 조건 검사 | NOT RUN 6 | 잠금 화면으로 전경 사전조건 불충족; 0 PASS |
| 실제 닫기·Esc·적용 키/마우스 입력 | NOT RUN | Windows 잠금 화면 확인 후 입력 중단 |
| 실제 반복 폴더 클릭·검색 해제·스티커 클릭/편집 | NOT RUN | 구성요소 검사와 구별 |
| 다른 앱 Alt+Tab·전경 목록 뒤 창 순서 | NOT RUN | 동일 스레드 검사로 대체하지 않음 |
| MSI 설치/업데이트·시작프로그램 등록·실사용 프로필 실행 | NOT RUN | 이번 포터블 시험 범위 밖 |
| 기존 사용자 데이터 | PASS | 실행 전후 파일 4개 SHA-256 불변 |

최종 구성요소 통과는 **126개**다. 초기 검증 도구 실행 중 오래된 테스트 바이너리가 전체 검사를 실행해 SV05 대기 시간 초과로 종료된 시도는 집중 검사의 통과 근거에서 제외했다. 이후 강제 재빌드하여 정확한 집중 진입점으로 원본과 수정본을 비교했다. 초기 수정의 첫 표시 실패 로그와 호출 스택도 보존한다.

## 격리와 재실행

구성요소 검사는 임시 SQLite/설정만 사용했다. `--manual-list-settings <fixture> <WorkBookmark.exe>` 호스트는 artifacts/manual-ui 아래 합성 데이터와 동일 해시의 제품 DLL을 사용한다. 목록/스티커는 실제 application context, 설정은 실제 SettingsForm과 합성 프로필 저장 콜백을 사용한다. OS 시작프로그램 등록은 실행하지 않는다.

UI 도구에서 직접 실행한 호스트 캡처에 Windows 잠금 화면이 나타나 입력을 보내지 않았고 시험 프로세스만 종료했다. 잠금 해제를 요청했으며 응답 없이 잠금 화면을 우회하지 않았다.

집중 명령: `dotnet run --project tests/WorkBookmark.Desktop.Tests -c Release -- --list-settings-only`, `--show-focus-component`, `--show-focus-only`. SDK는 global.json의 10.0.401이다. 마지막 플래그는 전경 조건 미충족 시 NOT RUN이다.

배포는 기존 `scripts/build.ps1 -SkipTests -Package -ArtifactsPath <별도 출력>`와 Commit 소스 모드를 사용한다. SkipTests는 위 집중 검사를 생략한 것이 아니라 무관한 전체 검사를 다시 실행하지 않는 선택이다. 배포 커밋은 SourceSnapshot.json, 배포 파일 검증은 릴리스 Validation.json·SHA256SUMS.txt를 따른다.
