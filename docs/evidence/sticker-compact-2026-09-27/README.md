# 0.2.5 스티커 제목·작은 크기 검증 기록

2026-09-27 · Windows x64 · 0.2.5 평가판 검증 기록

[관련 검사 로그](sticker-checks.log)에 **93개 통과**가 기록되어 있습니다. 대상은 SettingsDisplayChecks, StickerFormChecks, StickerInlineNoteChecks, StickerStartupChecks, StickerPersistenceChecks의 5개 클래스입니다. 실제 WinForms 컨트롤과 합성 입력·오류 주입, 임시 SQLite DB를 사용했습니다.

| 범위 | 현재 결과 |
|---|---|
| Release 빌드 | 경고 0개·오류 0개. [빌드 로그](build.log) |
| 관련 자동검사 | 최종 코드 93개 통과 |
| 100% 합성 화면 | 최종 코드 11개 렌더 종료 코드 0, 화면 확인 |
| 150% 합성 화면 | 최종 코드 fixture 5/5 통과, 측정 기록과 화면 확인 |
| 격리 호스트 시작·정상 종료 | 시작과 크기 적용 표식 확인, 종료 코드 0. 최종 화면 보완 전 관측 |
| 실제 마우스 편집·한글 IME·150% Windows 제목줄·테두리·다중 모니터 | 미검증 |
| 패키징·추출 파일·해시 | 기존 패키징 한 차례의 결과를 릴리스 첨부 validation.json·SourceSnapshot.json·SHA256SUMS.txt로 제공 |

150% 합성 화면에서 Font.Height로 계산한 제목 영역과 GDI가 그리는 두 줄 높이의 차이를 확인해 수정했습니다. 제목 높이와 관련 회귀 검사를 보완한 **최종 코드로 93개 검사와 100% 화면 11개·150% 화면 5개를 다시 확인**했습니다. 합성 검사는 실제 입력 시험과 구분합니다.

## 100% 배율에서 확인한 화면

- [기본 메모 제목](ui/sticker-default.png)
- [메모 없는 파일명](ui/sticker-empty-note.png)
- [웹 URL 대체 제목](ui/sticker-url-fallback.png)
- [폴더 주소 대체 제목](ui/sticker-folder-fallback.png)
- [긴 메모 제목](ui/sticker-long-note.png)
- [최소 크기](ui/sticker-minimum.png)
- [접힌 스티커](ui/sticker-collapsed.png)
- [재개 실패](ui/sticker-resume-error.png)
- [인라인 메모 편집](ui/sticker-inline-note.png)
- [메모 저장 실패와 초안](ui/sticker-inline-note-error.png)
- [최소 크기의 편집·실패 안내](ui/sticker-inline-note-minimum.png)

## 150% 합성 화면과 실제 입력의 구분

[측정 기록](metrics.json)은 96-DPI 모니터에서 WinForms 클라이언트 영역을 144 DPI로 구성한 합성 검사입니다. 이미지 확대 방식은 사용하지 않았으며 실제 150% Windows 제목줄·테두리와 모니터 전환은 검증하지 않았습니다. 아래 5개 fixture가 모두 통과했으며 제목 높이를 보완한 최종 화면을 확인했습니다.

- [기본](ui-150/default.png)
- [최소 크기의 긴 제목](ui-150/minimum-long-note.png)
- [최소 크기의 URL](ui-150/minimum-url.png)
- [최소 크기의 편집 오류](ui-150/minimum-edit-error.png)
- [접힌 상태](ui-150/collapsed.png)

[격리 호스트 시작 기록](native-startup.json)에서는 이전 외곽 크기 370×360이 276×209로 바뀌고 위치·원본·메모를 유지하며 적용 표식이 1로 저장되는 것을 확인했습니다. 호스트는 정상 종료했고 종료 코드는 0입니다. 이 역시 최종 제목 높이 보완 전 관측입니다.

컴퓨터 제어 도구가 SizableToolWindow 스티커를 선택 가능한 창 목록에 표시하지 않아 실제 스티커 입력은 수행하지 못했습니다. 따라서 마우스 제목 클릭·한글 IME 편집·창 전환 저장 실기와 다중 모니터 동작은 **미검증**으로 남깁니다. 시작·종료 확인을 입력 검증으로 분류하지 않습니다.

테스트 재개 전 잔여 WorkBookmark·dotnet·testhost·msiexec·MSBuild 프로세스가 없음을 확인했습니다. 격리 실기 호스트는 정상 종료했으며 사용자의 다른 앱을 종료하지 않았습니다.

이번 결과를 이전 버전의 자동 600개·설치 수명주기 118개·Desktop 209개와 합산하지 않습니다. 독립적인 Office·브라우저·IPC·캡처와 설치·제거·재부팅 반복 검사는 재실행하지 않습니다. 0.2.4의 설치 경로 보존 수정과 이전 검증 기록은 유지합니다.

기존 6종의 배포 파일은 [0.2.5 릴리스](https://github.com/prozac0401/BookMark/releases/tag/v0.2.5)에서 제공합니다. 패키지 구조·추출 파일 검사 결과는 첨부 [검증 JSON](https://github.com/prozac0401/BookMark/releases/download/v0.2.5/WorkBookmark-0.2.5-win-x64.validation.json), 소스 기준 커밋은 [SourceSnapshot.json](https://github.com/prozac0401/BookMark/releases/download/v0.2.5/SourceSnapshot.json), 파일 해시는 [SHA256SUMS.txt](https://github.com/prozac0401/BookMark/releases/download/v0.2.5/SHA256SUMS.txt)에서 확인합니다. 이 문서는 해당 패키지 검증과 UI 검증을 구분합니다.
