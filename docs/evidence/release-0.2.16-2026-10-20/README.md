# WorkBookmark 0.2.16 릴리스 검증

2026-10-20 Windows에서 스티커의 사각 실선 테두리 제거와 왼쪽 버튼 모서리 변경을 확인했습니다. DB v8과 설치 동작에는 변경이 없습니다.

## 빌드와 자동검사

`scripts/build.ps1 -ArtifactsPath ./artifacts/release-0.2.16/build`로 Release 빌드를 수행해 경고·오류 0개를 확인했습니다. 기본 자동검사의 서로 다른 항목은 아래와 같이 **1,024개 통과**했습니다.

| 범위 | 통과 | 실행 기록 |
|---|---:|---|
| Core/Storage | 76 | [전체 실행](build-tests.log) |
| IPC Integration | 17 | 전체 실행 |
| Desktop | 605 | 전체 실행 |
| Windows adapter | 10 | 전체 실행 |
| Excel evidence verdict | 13 | 전체 실행 |
| Capture session verdict | 14 | 전체 실행 |
| Word coordinates/protection | 24 | 전체 실행 |
| Office URL/resume | 103 | 전체 실행 |
| Browser accessibility adapter | 34 | 전체 실행 |
| Notepad recovery/coordinates | 28 | 전체 실행 |
| PDF synthetic DDE transport | 28 | 전체 실행 |
| Browser native host | 27 | 전체 실행 |
| Browser/SQLite integration | 15 | [단독 재실행](browser-sqlite-retry.log) |
| Browser extension Node tests | 23 | [별도 실행](extension-tests.log) |
| Registration ownership mocks | 7 | [별도 실행](registration-tests.log) |

최초 전체 명령은 Browser/SQLite의 `real SQLite reaches the pre-commit gate` 대기 시간 초과로 종료 코드 1을 반환했습니다. 실패 기록을 전체 로그에 보존했습니다. 소스 수정 없이 해당 검사만 단독 재실행해 15개 전부 통과(4,017ms)했고, 최초 명령이 도달하지 못한 Node·등록 소유권 검사는 별도로 실행해 통과했습니다. 최초 전체 명령 자체를 종료 코드 0으로 보고하지 않습니다.

창 핸들 생성 변경에 대한 별도 [표시 포커스 구성요소 검사](show-focus-component.log)는 **8개 통과, 미실행 0개**입니다. 기본 검사와 합계 **1,032개**이며, 같은 스레드의 활성 창·포커스 확인을 다른 앱 Alt+Tab이나 시스템 전경 순서 시험으로 확대하지 않습니다.

버튼의 둥근 영역이 `DrawToBitmap` 출력에서도 적용되도록 페인트 클리핑을 보완한 뒤 [최종 Release 빌드](paint-build.log) 경고·오류 0개, [스티커 집중 검사](sticker-final.log) **282개 통과**를 확인했습니다. 이 집중 검사는 위 기본 검사의 부분집합이므로 합계에 중복 가산하지 않습니다.

## 화면 확인과 한계

최종 빌드에서 `--render-stickers`와 `--render-folders`를 실행하고 다음 이미지를 직접 확인했습니다. 일반 바로가기와 폴더 버튼 모두 모서리가 둥글며 기존 한 줄 배치를 유지합니다.

- [기본 스티커](ui/sticker-default.png)
- [펼친 폴더](ui/folders-open.png)
- [스티커 안의 메모 편집](ui/sticker-inline-note.png)
- [처리 중 버튼](ui/sticker-shortcut-busy.png)

스티커는 선택·드롭 대상의 `DrawRectangle` 자체를 제거했습니다. 드롭 대상은 기존 배경색으로 구분합니다. Windows 11의 외곽선 숨김과 둥근 창 모서리는 DWM 속성 34의 `DWMWA_COLOR_NONE`, 속성 33의 `DWMWCP_ROUND`를 사용하며, 버튼은 8 DIP 반경을 화면 배율에 맞춰 적용합니다. 근거: [Microsoft DWM 속성](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute), [Windows 11 모서리 반경](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry).

`DrawToBitmap`은 DWM이 합성하는 창 외곽선·모서리·그림자를 재현하지 않습니다. 이미지의 사각 네이티브 프레임은 실제 DWM 외곽선 확인 증거가 아닙니다. 실제 창은 임시 데이터 미리보기로 실행했으나 computer-use가 `failed to activate captured window`를 반환했고 창 목록을 새로 읽은 뒤 재시도해도 같았습니다. 실제 마우스·키보드 조작, DWM 합성 화면 확인, 한글 IME 실입력과 서로 다른 배율의 모니터 간 이동은 **NOT RUN**입니다. 사용자 프로필 데이터는 이번 확인에 사용하지 않았습니다.

## 패키지 검증

검사 후 소스와 문서를 커밋한 깨끗한 작업 트리에서 `scripts/build.ps1 -SkipTests -Package -Msi -ArtifactsPath ./artifacts/release-0.2.16/build`로 최종 패키지를 생성합니다. SkipTests는 위 자동검사 반복만 생략하며 MSI 구조 검사·관리 추출과 전체 파일 SHA-256 대조는 수행합니다. 최종 소스 커밋은 `SourceSnapshot.json`, MSI 결과는 `WorkBookmark-0.2.16-win-x64.validation.json`, 배포 파일 지문은 `SHA256SUMS.txt`를 기준으로 합니다.

관리 추출은 Windows Installer 등록이나 실제 설치를 수행하지 않습니다. 실제 MSI 설치·업데이트·제거·재부팅과 실사용 프로필 실행은 **NOT RUN**입니다. 패키지는 미서명입니다.
