# 0.2.11 트리·색상 검증 기록

2026년 10월 14일, 기존 main `18d118e6d3f4d131ee3db6bec2a345fe51aa7484` 이후 트리·색상 변경을 검증했다. 사용자는 검증과 릴리스를 요청했다. 실사용 책갈피·설정은 변경하지 않고 임시 SQLite와 저장소 하위 격리 fixture를 사용했다.

## 전체 자동검사

실행: `./scripts/build.ps1`. [.NET Release 빌드와 전체 실행 로그](build-tests.log)의 종료 코드는 0이다. 경고·오류 0개, 아래 **866개가 모두 통과**했다. 집중 검사 재실행이나 과거 릴리스 결과는 중복 합산하지 않았다.

| 범위 | 통과 |
|---|---:|
| Core/Storage (트리 9, 색상 6 포함) | 67 |
| IPC Integration | 17 |
| Desktop (트리 51, 색상 UI 12, 색상 연동 6 포함) | 456 |
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

트리 검사는 부모 변경·분리·다단계·순환 거부, 삭제/복원, 늦은 목록 읽기, 전체 항목 검색, 네이티브 이동 메시지·취소·크기 조절, 하위 이동과 초안 보존, 목록 변경 후 재시작 배치를 포함한다. 색상 검사는 각 색상과 기본 복원, 양쪽 UI 동기화, 메뉴 체크, 실패 롤백, 편집 초안·배치 유지, DB v5/v6 이전 및 백업을 포함한다.

## 화면 확인

`--render-hierarchy`로 실제 WinForms 컨트롤을 렌더링했다. [목록 트리](recent-tree.png), [스티커 트리](sticker-tree.png), [색상 목록](list-colors.png), [색상 스티커](sticker-colors.png)를 직접 확인했다. 화면 렌더링은 실제 마우스 입력 시험을 대신하지 않는다.

## 실기와 패키지 범위

실제 UI 시험은 `--manual-tree-colors` fixture에서 시도했지만 **NOT RUN**이다. 임시 fixture는 당시 게시용 제품 DLL과 같은 SHA-256(`2a5a50e4eea7bc47ebbd76bbd993b5964d96bb7d67bd6ac3753f4c5a924f1698`)을 사용했다. 최초 실행에서 입력 도구가 창을 찾지 못해 해당 시험 프로세스만 종료하고, 입력 도구로 직접 실행할 수 있는 격리 런처로 재시도했다. 두 번째 실행에서는 검증 호스트가 검색됐지만 창 활성화가 실패했고 화면 캡처에는 Windows 잠금 화면이 표시됐다. 즉시 입력을 중단했으며 부모 변경·색상 저장 시도는 모두 0회였다. 드래그·색상 메뉴의 실제 입력 성공으로 집계하지 않는다.

이후 최종 코드 검토에서 발견한 목록 모드 색상 변경 후 배치 보존 문제는 자동 회귀검사로 검증했다. 위 DLL은 그 수정 전의 실기 시도 식별용이며 최종 배포 DLL 식별값은 릴리스의 검증 JSON을 따른다. 호스트는 저장소 관찰·제품 창 표시·재시작·실패 주입만 제공하며, 제품 입력을 대신 실행하지 않는다.

MSI 실제 설치·업데이트·제거·재부팅, 혼합 DPI, 한글 IME, 설치된 Office·브라우저의 종단 실기는 이번 실행 범위에 포함하지 않는다. MSI 구조·관리 추출 결과는 릴리스의 검증 JSON으로 별도 제공한다.
