# WorkBookmark 0.2.14 릴리스 검증

2026-10-15 Windows에서 `138b2fe`의 제품 코드에 정식 버전 `0.2.14`와 배포 문서 갱신을 적용하여 검사했다. 제품 동작·DB v8·설치 코드에는 추가 변경이 없다. 이전 시험판의 검사 결과를 이번 결과로 재사용하지 않았다.

## 전체 자동검사

`scripts/build.ps1 -ArtifactsPath ./artifacts/release-0.2.14/build` 종료 코드 0, Release 빌드 경고·오류 0개, **981개 통과**. [전체 실행 로그](build-tests.log).

| 범위 | 통과 |
|---|---:|
| Core/Storage | 76 |
| IPC Integration | 17 |
| Desktop | 562 |
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

기본 Desktop 실행에 포함되지 않은 `--show-focus-component`도 같은 빌드에서 별도로 실행해 **8개 통과, 미실행 0개**를 확인했다. [포커스 구성요소 로그](show-focus-component.log). 총 **989개**이며 목록·설정 집중검사를 중복 집계하지 않았다.

`--show-focus-only`는 실제 시스템 전경 창과 포커스 사전조건을 충족하지 못하여 **통과 0개, NOT RUN 6개**였다. [전경 검사 로그](show-focus-native.log). 종료 코드가 0이어도 이를 통과로 집계하지 않았다. 이번에는 잠금 화면 여부를 별도로 확인하지 않았으므로 원인을 잠금으로 단정하지 않는다.

## 패키지 검증의 기준

배포 패키지는 검사 후 문서를 포함해 커밋한 깨끗한 소스에서 `scripts/build.ps1 -SkipTests -Package -Msi -ArtifactsPath ./artifacts/release-0.2.14/build`로 재빌드한다. SkipTests는 위 전체 자동검사를 반복하지 않는 옵션이며 MSI 구조 검사와 관리 추출·전체 파일 SHA-256 대조는 생략하지 않는다. 최종 소스 커밋은 `SourceSnapshot.json`, 최종 MSI 결과는 `WorkBookmark-0.2.14-win-x64.validation.json`, 배포 파일 지문은 `SHA256SUMS.txt`에 기록한다. 이 문서 작성 후 만들어지는 최종 패키지 결과를 사전 통과로 주장하지 않는다.

관리 추출은 Windows Installer 등록이나 실제 설치를 수행하지 않는다. 실제 MSI 설치·업데이트·제거·재부팅, 실사용 프로필 실행, 물리적 마우스·키보드 입력, 다른 앱 Alt+Tab과 실제 전경 창 순서, 혼합 DPI·한글 IME 실기는 **NOT RUN**이다. 구성요소 검사와 실제 사용자 조작을 구별한다. 패키지는 미서명이다.
