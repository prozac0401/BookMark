# WorkBookmark 0.2.15 릴리스 검증

2026-10-19 Windows에서 스티커 조작 개선과 정식 버전 `0.2.15`를 적용한 소스를 별도 obj/bin 경로로 빌드하고 검사했다. 바로가기·폴더 버튼의 왼쪽 배치, 클릭 선택·끌기 이동·명시적 편집, 크기 조절 손잡이, 편집 전후 크기 보존, 자석 모드와 Shift 묶기 동작이 이번 변경 범위다. DB v8과 설치 동작에는 변경이 없다. 이전 릴리스의 검사 결과를 이번 결과로 재사용하지 않았다.

## 전체 자동검사

`scripts/build.ps1 -ArtifactsPath ./artifacts/release-0.2.15/build` 종료 코드 0, Release 빌드 경고·오류 0개, **1,024개 통과**. [전체 실행 로그](build-tests.log).

| 범위 | 통과 |
|---|---:|
| Core/Storage | 76 |
| IPC Integration | 17 |
| Desktop | 605 |
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

기본 Desktop 실행에 포함되지 않은 `--show-focus-component`도 같은 빌드에서 별도로 실행해 **8개 통과, 미실행 0개**를 확인했다. [포커스 구성요소 로그](show-focus-component.log). 총 **1,032개**이며, 앞서 수행한 개발 중 검사와 집중검사는 중복 집계하지 않았다.

포커스 구성요소 검사는 같은 스레드의 활성 창·포커스 컨트롤과 스티커 표시 전후 상태를 확인한다. 다른 앱 Alt+Tab, 실제 시스템 전경 창의 순서와 물리적 입력에 대한 통과를 뜻하지 않는다. 이번 릴리스 검증에서 실제 전경 조건 검사 `--show-focus-only`를 실행한 것으로 집계하지 않는다.

## 화면과 실제 조작의 확인 범위

스티커 화면 배치는 UI 구성요소 렌더링으로 확인했다. 아래 그림은 실제 사용자 입력의 성공 증거와 구분한다.

- [기본 스티커](ui/sticker-default.png)
- [스티커 안의 메모 편집](ui/sticker-inline-note.png)
- [메모 저장 실패 상태](ui/sticker-inline-note-error.png)
- [펼친 폴더](ui/folders-open.png)

실제 마우스·키보드 조작은 Windows 도구의 캡처한 창 활성화 실패로 완료하지 못했다. 실제 한글 IME 입력, 서로 다른 배율의 모니터 간 이동·크기 조절, 다른 앱 Alt+Tab과 실제 전경 창 순서는 **NOT RUN**이다. 구성요소 검사와 렌더링을 실제 사용자 조작으로 확대하지 않는다.

## 패키지 검증의 기준

배포 패키지는 검사 후 문서를 포함해 커밋한 깨끗한 소스에서 `scripts/build.ps1 -SkipTests -Package -Msi -ArtifactsPath ./artifacts/release-0.2.15/build`로 재빌드한다. SkipTests는 위 전체 자동검사를 반복하지 않는 옵션이며 MSI 구조 검사와 관리 추출·전체 파일 SHA-256 대조는 생략하지 않는다. 최종 소스 커밋은 `SourceSnapshot.json`, 최종 MSI 결과는 `WorkBookmark-0.2.15-win-x64.validation.json`, 배포 파일 지문은 `SHA256SUMS.txt`에 기록한다. 이 문서 작성 후 만들어지는 최종 패키지 결과를 사전 통과로 주장하지 않는다.

관리 추출은 Windows Installer 등록이나 실제 설치를 수행하지 않는다. 실제 MSI 설치·업데이트·제거·재부팅과 실사용 프로필 실행은 **NOT RUN**이다. 패키지는 미서명이다.
