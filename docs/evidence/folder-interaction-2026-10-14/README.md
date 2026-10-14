# 0.2.13 폴더 이름 편집·이동 검증

2026년 10월 14일, 0.2.12 커밋 `d33acee80be99c7c10640a1b235cddcbd94ffa8a` 이후 사용자가 보고한 폴더 이름 변경·이동 문제를 수정했다. 임시 SQLite와 합성 책갈피를 사용했으며 실사용 책갈피·설정을 수정하지 않았다. DB v8을 유지한다.

## 수정과 회귀검사

- 일반 스티커와 폴더 모두 제목 클릭·Ctrl+E는 해당 창 안의 편집기를 연다. 폴더 F2도 이름 편집을 연다. 오른쪽 버튼·Ctrl+Enter는 기존 작업 열기 또는 폴더 열기·닫기를 실행한다.
- 제목에서 시스템 드래그 임계거리를 넘으면 창 이동을 시작한다. 이동 중이나 늦게 도착한 버튼 해제의 클릭을 억제하고, 다음 편집 입력은 허용한다. 폴더 이동 취소 시 자식 위치도 복구한다.
- 폴더 이름은 1~100자 정책을 적용하고 Enter·자동 저장·Esc·취소를 기존 메모 편집과 공유한다. 색상 갱신·저장 실패·늦은 응답·편집 중 이동에도 초안과 정상 배치를 유지한다.
- AppContext의 실제 이름 변경 이벤트부터 저장소·목록·스티커 반영까지 검사한다. 닫힌 부모 아래의 폴더 편집은 별도 창으로 처리하며 자식을 노출하지 않는다.
- 목록에서 여러 폴더의 편집창을 열어도 초안과 저장 대상이 섞이지 않는다. 취소 버튼에 초점이 있을 때 Enter는 저장 대신 취소한다.

폴더 집중검사는 **94개 통과**했다: 목록 23, 스티커 37, 이름 창 7, 앱 연동 27. [집중검사 로그](focused-checks.log). 이 검사는 전체 실행에도 포함하므로 합산할 때 중복 집계하지 않는다.

## 최종 전체 실행

`./scripts/build.ps1` 종료 코드 0, Release 빌드 경고·오류 0개, **970개 모두 통과**했다. [전체 실행 로그](build-tests.log). Desktop에는 이전 521개에 이번 회귀검사 30개를 더한 551개가 포함된다.

| 범위 | 통과 |
|---|---:|
| Core/Storage | 76 |
| IPC Integration | 17 |
| Desktop | 551 |
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

## 입력 검사와 화면 확인의 범위

제목 제스처 검사는 실제 WinForms 컨트롤의 마우스 이벤트와 창 메시지를 주입한다. 이동 시작 시 capture 해제, 임계거리 내 떨림, capture 상실·오른쪽 버튼, 이동 중 클릭, `WM_LBUTTONUP` 처리 도중 들어오는 반사 클릭 메시지의 억제를 확인한다. 물리적 마우스를 움직여 끝까지 드래그한 결과는 아니다.

초기 네이티브 클릭 재현은 실패했다. 테스트 메시지 좌표와 실제 포인터 위치를 맞춘 뒤에도 Windows의 대상 창 조회가 시험 컨트롤과 다른 창을 반환했으며, 합성 이동 메시지는 실제 전역 마우스 버튼 상태를 만들지 않는다. 이 환경의 입력 데스크톱에 의존하지 않는 구성요소 검사로 범위를 명시하여 검증했다. 실제 마우스 조작의 통과로 대체하지 않는다.

실제 WinForms 컨트롤을 렌더링한 [폴더 이름 편집](folder-rename.png), [중첩 폴더](folders-nested-open.png)를 확인했다. 편집 창은 메모와 같은 입력·자동 저장 안내·취소 구성을 사용하며 정상 크기로 복귀한다. 이 이미지는 컨트롤 합성이며 실제 입력 성공의 증거가 아니다.

실제 마우스 클릭·드래그, 혼합 DPI·한글 IME 실기, MSI 실제 설치·업데이트·제거·재부팅은 **NOT RUN**이다. 최종 MSI 구조·관리 추출·파일 SHA-256 대조는 릴리스의 검증 JSON으로 제공하며 관리 추출은 실제 설치가 아니다. SourceSnapshot.json은 소스 커밋을, SHA256SUMS.txt는 배포 파일 지문을 기록한다.
