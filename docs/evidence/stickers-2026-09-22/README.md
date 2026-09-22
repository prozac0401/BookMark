# 0.2.0 스티커 구현·검증 기록

2026-09-22 · Windows x64 · .NET SDK 10.0.401

목록과 독립 포스트잇 창이 같은 책갈피 ID를 사용합니다. 설정에서 표시 방식을 선택하고, 이동·크기·접기·항상 위 상태를 DB v5에 저장합니다. 지우기는 공유 기록의 논리 삭제이며, 닫기·Esc는 창만 숨깁니다. 최근 삭제에서 메모와 배치를 유지한 채 복원할 수 있습니다.

## 자동 검증

Release 빌드 **경고 0개·오류 0개**, 자동 검사 **529개 통과**. 아래는 실제 실행 로그이며, 제품의 전체 실기 인수 또는 장기 사용성 평가를 통과했다는 뜻은 아닙니다.

| 검사 | 통과 | 실행 로그 |
|---|---:|---|
| Core / SQLite / 마이그레이션 | 52 | [core-storage.log](tests/core-storage.log) |
| 실제 IPC 프로세스 | 17 | [ipc.log](tests/ipc.log) |
| Desktop 구성요소·스티커·설정·저장 경합 | 138 | [desktop.log](tests/desktop.log) |
| Windows 어댑터 | 10 | [adapter-checks.log](tests/adapter-checks.log) |
| Excel 증거 판정 / 수동 세션 판정 | 13 / 14 | [evidence-selftest.log](tests/evidence-selftest.log), [session-selftest.log](tests/session-selftest.log) |
| Word 좌표 / Office URL | 24 / 103 | [word-checks.log](tests/word-checks.log), [office-url-checks.log](tests/office-url-checks.log) |
| 브라우저 어댑터 / 메모장 / PDF | 30 / 28 / 28 | [browser-checks.log](tests/browser-checks.log), [notepad-checks.log](tests/notepad-checks.log), [pdf-checks.log](tests/pdf-checks.log) |
| Native messaging / 브라우저·SQLite | 27 / 15 | [browser-native.log](tests/browser-native.log), [browser-sqlite.log](tests/browser-sqlite.log) |
| 확장 JavaScript / 등록 소유권 모형 | 23 / 7 | [extension.log](tests/extension.log), [browser-registration.log](tests/browser-registration.log) |

[최종 빌드 로그](tests/build.log). 기존 실행 파일 잠금과 충돌하지 않도록 `.artifacts/sticker-v020b`에서 빌드하고 동일 출력의 검사 EXE를 직접 실행했습니다. Desktop 검사는 지속적인 WinForms 메시지 루프 안에서 실행하여 실제 앱과 같은 UI 스레드로 비동기 결과를 처리합니다. 임시 SQLite와 합성 문서를 사용했으며 사용자의 책갈피 DB를 변경하지 않았습니다.

새 검사는 기존 설정의 목록 기본값, 즉시 적용·취소·실패, 25개 이상의 스티커 표시, ID별 갱신, 크기 저장과 재시작, 숨기기와 삭제의 구분, 연속 삭제·되돌리기, 시간이 지난 뒤 복원, 삭제와 재갈무리의 응답 순서 역전, 편집 중 스티커 삭제, 저장 중 이동·실패·종료를 다룹니다. 음수 모니터 좌표와 150% 배율 배치 보정은 계산 단위에서 확인했습니다.

## 실제 컨트롤 렌더링

실제 WinForms 창을 합성 데이터로 표시하고 그린 100% 배율 이미지입니다. 기본·최소 크기, 500자 메모의 스크롤, 접기, 설정 선택과 기존 목록을 확인했습니다.

- [기본 스티커](ui/sticker-default.png) · [최소 크기](ui/sticker-minimum.png) · [500자 메모](ui/sticker-long-note.png) · [접힌 스티커](ui/sticker-collapsed.png)
- [설정](ui/settings.png) · [목록](ui/recent-bookmarks.png) · [소개 알림](ui/toast-introduction.png) · [긴 경로 알림](ui/toast-long-korean-path.png)

## 배포와 남은 실기

릴리스의 MSI 검증 JSON은 설치 범위·업그레이드·데이터 보존·아이콘 및 관리 이미지 추출 후 파일 해시 검사를 기록합니다. `SourceSnapshot.json`과 `SHA256SUMS.txt`로 배포 소스 커밋과 파일을 확인할 수 있습니다. 실제 설치 또는 기존 설치 교체는 관리 이미지 추출 검사와 구분합니다.

이 세션의 실제 화면 조작 연결은 사용할 수 없었습니다. 마우스로 이동·크기 조절하기, 서로 다른 배율의 모니터 사이 이동, 실제 한국어 IME·키보드 사용, 다량 스티커의 장시간 성능, Office·회사 인증, 실제 MSI 설치·업그레이드·로그인 재실행은 별도 실기 항목입니다. 이전 [호환성 기록](../../compatibility.md)의 미검증 항목을 이번 자동 검사로 통과 처리하지 않았습니다.
