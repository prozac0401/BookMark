# WorkBookmark 0.2.18 릴리스 검증

2026-11-02 Windows에서 자석 드래그의 누적 이동과 트리 전체 바운딩박스 회피·복구를 검증했습니다. DB v8과 기존 단축키·폴더 관계는 유지합니다.

## 빌드와 자동검사

`scripts/build.ps1 -ArtifactsPath ./artifacts/release-0.2.18/build`의 Release 빌드는 **경고 0개·오류 0개**입니다. Core/Storage와 IPC 검사까지 통과한 뒤 Desktop 검사 실행 파일의 최초 시작에서 Windows가 접근을 거부해 이 최초 명령은 종료 코드 1로 끝났습니다. [최초 빌드·검사 로그](build-tests.log).

같은 빌드의 실행 파일을 다시 시작하자 정상 실행됐습니다. 동일 산출물로 원래 스크립트의 Desktop 이하 검사들을 순서대로 이어 실행했고 **종료 코드 0**을 확인했습니다. [전체 후속 검사 로그](continued-tests.log). 제품 코드나 실행 권한·보안 설정을 변경하지 않았습니다.

진단 중 DLL을 직접 실행한 시도는 테스트가 자신의 EXE를 자식 worker로 실행한다고 가정하기 때문에 SO04에서 실패했습니다. [DLL 실행 진단 로그](desktop-dll-diagnostic.log)를 보존하며, 이 시도는 통과 수에 포함하지 않습니다. 최종 EXE 실행에서는 해당 worker 경로를 포함한 Desktop 전체 검사가 통과했습니다.

| 범위 | 통과 |
|---|---:|
| Core/Storage | 76 |
| IPC Integration | 17 |
| Desktop | 672 |
| Windows adapter | 10 |
| Excel evidence verdict | 13 |
| Capture session verdict | 14 |
| Word coordinates/protection | 24 |
| Office URL/resume | 103 |
| Browser accessibility adapter | 34 |
| Notepad recovery/coordinates | 28 |
| PDF synthetic DDE transport | 28 |
| Browser native host | 27 |
| Browser/SQLite integration | 15 |
| Browser extension Node tests | 23 |
| Registration ownership mocks | 7 |
| 기본 검사 합계 | **1,091** |

별도 [표시 포커스 구성요소 검사](show-focus-component.log)는 **8개 통과·미실행 0개**이며 전체 합계는 **1,099개**입니다. 같은 UI 스레드의 활성 창과 포커스 검사이며, 다른 앱의 Alt+Tab이나 시스템 전경 전환을 검증했다는 뜻은 아닙니다. 개발 중 집중 검사와 실행 진단 재검사는 합계에 중복 산입하지 않습니다.

## 이번 변경의 확인 범위

- 자석에 붙은 뒤 한 픽셀씩 움직여도 실제 마우스 위치 기준 이동량이 누적되어 네 방향으로 벗어나는지, Alt 전환과 드래그 종료 후 상태를 확인합니다.
- 트리 안의 빈 공간을 포함한 전체 사각형을 확보하고, 주변 트리와 숨겨진 자식들이 같은 거리로 이동하는지 확인합니다.
- 중첩·동시 확장, 화면 경계·음수 좌표·DPI·다른 모니터로 이어지는 예약 영역, 서로 막힌 트리의 함께 이동과 공간 부족 시 원위치 보존을 확인합니다. 다중 모니터 검사는 기하 입력 기반이며 실제 모니터 간 드래그 시험은 아닙니다.
- 접기·숨기기·재시작 때의 복구, 임시 위치 저장 방지, 수동 이동·취소, 편집·크기 변경에 따른 재계산, 임시 이동된 대상에 새 폴더를 만들 때의 표시 위치 보존을 확인합니다.

실제 마우스·키보드 조작, 한글 IME 실입력, 다른 앱 Alt+Tab, 여러 배율의 실제 모니터 간 이동은 **NOT RUN**입니다. 이번 변경은 화면 디자인을 바꾸지 않았으며 새 렌더링 결과를 실기 증거로 제시하지 않습니다.

## 패키지 검증

검사를 마친 소스·문서·기록을 커밋한 깨끗한 작업 트리에서 `scripts/build.ps1 -SkipTests -Package -Msi -ArtifactsPath ./artifacts/release-0.2.18/build`로 최종 배포 파일을 생성합니다. 기본 `Commit` 모드로 소스 ZIP을 만들며, MSI 구조 검사와 관리 추출·전체 파일 SHA-256 대조는 생략하지 않습니다.

최종 소스 커밋은 첨부 `SourceSnapshot.json`, MSI 결과는 `WorkBookmark-0.2.18-win-x64.validation.json`, 배포 파일 지문은 `SHA256SUMS.txt`를 기준으로 합니다. 관리 추출은 실제 사용자 설치를 수행하지 않습니다. 실제 MSI 설치·업데이트·제거·재부팅과 실사용 프로필 실행은 **NOT RUN**이며 패키지는 미서명입니다.
