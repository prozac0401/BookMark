# 0.2.12 폴더 책갈피 검증 기록

2026년 10월 14일, 0.2.11 커밋 `c92ec142abcfdba3c7e108abcc0c461dfd016425` 이후 폴더 요구사항을 구현했다. 임시 SQLite·합성 문서를 사용했으며 실사용 책갈피·설정은 수정하지 않았다.

## 자동검사

최종 `./scripts/build.ps1` 종료 코드 0, Release 빌드 경고·오류 0개이며 **940개 모두 통과**했다. [전체 실행 로그](build-tests.log). 폴더 집중 검사 재실행과 이전 릴리스 결과는 중복 집계하지 않았다.

| 범위 | 통과 |
|---|---:|
| Core/Storage (폴더 9개 포함) | 76 |
| IPC Integration | 17 |
| Desktop (폴더 목록 23·스티커 22·이름 6·연동 14 포함) | 521 |
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

## 확인 범위

- 폴더 또는 일반 책갈피 위로 드롭할 때의 서로 다른 처리, 중첩 이동·순환 거부, 저장 실패 시 새 폴더와 관계 전체 롤백.
- 링크 없는 폴더를 외부 실행·재캡처·경로 변경 요청에 사용하지 못하도록 검증.
- 폴더 열기·닫기와 중첩 상태의 저장, 두 화면 동기화, 닫힌 폴더 검색, 삭제·복원 시 자식 접근.
- 폴더 생성·이름·색상 변경, 이름 저장 실패 재시도, 키보드 생성, 늦은 읽기와 새 변경의 순서 보존.
- 숨겨진 자식 메모 초안·사용자 배치·개별 숨김·전체 숨김 유지.
- 목록에서 폴더를 만든 뒤 스티커를 표시하지 않고 재시작해도 대상 스티커의 저장 위치를 폴더가 이어받는지 확인.
- DB v7→v8 이전과 실패 롤백·백업, 기존 링크·메모·색상·삭제 이력·스티커 배치, 외부 FK·인덱스·트리거 보존. 알 수 없는 칼럼이 있으면 데이터를 버리지 않고 이전 거부.

## 화면 확인

실제 WinForms 컨트롤을 `--render-folders`로 렌더링했다. [닫힌 목록 폴더](list-folders-closed.png), [열린 목록 폴더](list-folders-open.png), [닫힌 스티커 폴더](folders-closed.png), [열린 폴더](folders-open.png), [중첩 폴더](folders-nested-open.png)를 직접 확인했다. 이 이미지는 실제 마우스 입력 성공의 증거가 아니다.

## 실기와 배포 범위

실제 마우스 드래그·메뉴 입력은 이번 폴더 변경에서 **NOT RUN**이다. 앞선 0.2.11 실기 시도에서 Windows 잠금 화면이 확인되어 입력을 중단했고, 폴더 변경 이후에는 실기 입력을 수행하지 않았다. 혼합 DPI·한글 IME·MSI 실제 설치·업데이트·제거·재부팅 및 Office·브라우저 종단 실기도 이번 변경으로 새로 인증하지 않는다.

최종 MSI 구조·관리 추출·전체 파일 SHA-256 대조 결과는 릴리스에 동봉한 검증 JSON으로 제공한다. 관리 추출은 실제 설치가 아니다. 소스 커밋은 SourceSnapshot.json, 배포 파일의 지문은 SHA256SUMS.txt를 기준으로 한다.
