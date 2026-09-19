# 내부 위치 및 브라우저 확장 요구명세

> 2026-09-19 추가 변경: 사용자 점검 결과에 따라 메모장 지원을 추가하고 Word·PowerPoint 실제 저장 오류와 브라우저 안내를 수정합니다. 메모장의 명시적 파일 선택·문자 위치 계약과 검증은 [0.1.1 수정 기록](capture-fixes.ko.md)을 따릅니다.

작성: 2026-09-19. 근거: 사용자의 추가 구현 요청, 우선순위 없음, 이어진 “edge, chrome 브라우저등도 지원이 필요해요. 요구명세에 이를 저장하고 구현” 요청.

이 문서는 원본 명세의 웹/Word/PPT/PDF 후속 제외 문구를 이번 구현 범위에서 대체한다. 기존 탐색기·Excel의 필수 수용시험은 유지한다. 구현됨과 설치된 제품에서 실기 통과함을 구분한다.

## 대상별 계약

| 대상 | 저장 | 재개 | 경계 |
|---|---|---|---|
| Word | 저장된 로컬 문서 경로, 본문 선택 시작 문자 오프셋(0부터), 미저장 변경 여부 | 같은 문서 본문에서 해당 문자 위치 선택 | 본문만. 머리글·각주·도형·블록·분할/보호 보기 거절. 문서 편집 후 원래 문장 추적 보장 없음 |
| PowerPoint | 저장된 로컬 발표 파일 경로, SlideID, 당시 슬라이드 번호, 미저장 변경 여부 | 현재 파일에서 ID를 다시 찾아 이동 | 일반 편집만. 재정렬에도 ID로 찾고 삭제된 ID는 임의 대체하지 않음 |
| PDF | PDF 경로, 물리 페이지 번호(1부터) | 확인 가능한 뷰어에 지정 페이지 이동 후 실제 페이지 재확인 | SumatraPDF 3.7+ API 기능 확인을 전제로 한 실험 구현. Adobe 및 브라우저 PDF 내부 페이지는 아직 미지원 |
| Microsoft Edge / Google Chrome | 사용자가 지정한 활성 탭의 제목과 HTTP/HTTPS URL | Windows 기본 브라우저로 URL 열기 요청 | 공통 Manifest V3 확장 + 로컬 Native Messaging 호스트 설치 필요 |
| 그 밖의 Chromium 브라우저 | 동일 확장 계약을 따를 수 있는 후보 | 위와 동일 | 제품별 설치/실기 확인 전 지원 완료 표시 금지 |

## 브라우저 MUST

- 확장 버튼에서 저장하거나 브라우저 확장 단축키(기본 Ctrl+Shift+Y)로 실행한다. 기존 전역 Ctrl+Alt+B는 Office/탐색기용이다. 충돌하면 브라우저의 확장 단축키 설정에서 변경한다.
- 명시적인 사용자 동작에서만 activeTab 권한으로 현재 탭을 읽는다. 권한은 activeTab + nativeMessaging으로 제한한다. 모든 사이트 읽기, history, cookies, debugger, DOM/본문 수집 권한을 요구하지 않는다.
- 저장 직전에도 같은 활성 탭/URL인지 확인한다. 바뀌면 실패하고 재입력을 안내한다. 순간 스냅샷 이후의 이동은 이미 저장한 URL을 바꾸지 않는다.
- URL은 최대 16,384 UTF-16 문자, 제목은 최대 256 문자. HTTP/HTTPS 절대 URL만 받는다. 자격증명 포함 URL, 제어문자, 역슬래시, 파일·javascript·data·브라우저 설정 URL은 거절한다. 비공개 창과 이동 중인 탭도 이번 확장에서 거절한다.
- 쿼리와 fragment를 포함한 URL 원문을 보존한다. 동일 원문 URL은 한 책갈피로 합치고 제목·저장시각만 갱신하며 기존 메모를 유지한다. 서로 다른 query/fragment는 다른 위치다. 제목은 식별키가 아니다.
- 제목이 없으면 호스트명을 표시한다. URL에 민감한 쿼리가 있으면 그대로 로컬 DB에 포함됨을 설치 안내에 알린다. 진단 로그에 제목/URL을 남기지 않는다.
- 호스트는 길이 제한이 있는 표준입출력 메시지만 처리하고 같은 Windows 사용자·로그인 세션의 앱으로 전달한다. 명령행/쉘 문자열에 제목·URL을 넣지 않는다. 앱을 먼저 실행해야 한다.
- DB 커밋 완료 응답(requestId 일치, success=true, code=CaptureCommitted) 뒤에만 성공을 표시한다. 시간초과/연결 종료의 결과는 미확인으로 안내하고 자동 재시도하지 않는다.
- 앱이 종료 중이거나 다른 작업 중이면 실패를 반환한다. 브라우저 캡처도 기존 저장소·메모·검색·삭제·되돌리기를 공유한다.
- 최근 목록에서 Enter 시 URL을 기본 브라우저로 열기 요청한다. 페이지 로드 성공·로그인 복원·원래 탭 재사용·스크롤 복원은 주장하지 않는다. URL 항목에 파일 경로 재지정 창을 띄우지 않는다.
- 설치는 실제 브라우저별 확장 ID를 지정해 HKCU NativeMessagingHosts에만 등록한다. wildcard origin, 시스템 범위 등록, 타 설치 덮어쓰기, 브라우저 정책 우회는 하지 않는다. 제거는 해당 설치가 소유한 항목만 처리한다.

## 상황과 결정 근거

1. 주소창 UI 읽기는 앱 버전/포커스/주소 생략 방식에 의존한다. Edge와 Chrome 공통의 확장 API를 사용하면 사용자가 선택한 탭의 URL을 명시적으로 받을 수 있어 확장 방식을 선택했다.
2. 기본 브라우저로 재개하면 사용자의 기존 연결 설정을 따른다. 저장한 브라우저 강제 실행은 설치 경로 탐색과 별도 프로필 선택을 요구하므로 이번 계약에 포함하지 않았다.
3. query와 fragment를 없애면 같은 웹 앱의 다른 문서·탭·위치가 합쳐질 수 있다. 의미를 추측하지 않고 원문을 저장한다.
4. Word는 문자를 읽거나 문서에 책갈피를 삽입하지 않고 숫자 오프셋만 저장한다. PowerPoint는 번호가 재정렬에 따라 바뀌므로 안정적인 SlideID를 저장한다. 어느 어댑터도 원본 Save/Close/Quit 명령을 보내지 않는다.
5. 현재 PDF 기본 연결은 Adobe Acrobat이고 Sumatra 설치는 발견하지 못했다. 현재 파일·페이지와 foreground 연결을 확인하지 못한 제품에는 성공을 추측하지 않는다. PDF 실험 코드는 기본 연결을 변경하지 않는다.
6. 스키마 1→2는 기존 DB의 사전 백업 후 트랜잭션으로 진행한다. 기존 기록·메모·삭제/재개 이력·정렬 sequence를 유지한다. 새 버전 DB를 이전 앱으로 열지 않는다. 이전 버전 복귀는 사전 백업을 별도 보존해 수행한다.

## 수용시험

| ID | 추가 검증 |
|---|---|
| B01 | Edge/Chrome 각각 확장 설치·호스트 등록·HTTP/HTTPS 저장·최근 목록·Enter 열기 |
| B02 | 동일 URL 다른 제목 재저장 시 기존 메모와 ID 유지; query/fragment 차이는 분리 |
| B03 | 다른 창/탭 전환 중 캡처, 금지 scheme/자격증명/긴 메시지/변조 응답 거절 |
| B04 | 앱 미실행/바쁨/DB 쓰기 실패/응답 단절에서 성공 오표시 없음 |
| B05 | 두 브라우저 실제 ID만 허용; -WhatIf와 제거의 소유권 확인; 다른 설치 보존 |
| B06 | 페이지 본문·쿠키·기록 접근 없음; URL/제목 진단 로그 미포함 |
| W01 | Word 본문 위치 저장·열린/닫힌 문서 재개·문서 축소·비본문·다중창·보호 보기 |
| P01 | PPT 슬라이드 저장·재정렬·삭제·다중창·슬라이드 쇼/보호 보기·원본 불변 |
| F01 | 지원 PDF 뷰어 실제 파일·페이지 확인, 거짓 DDE ACK/다중프레임/지원 API 없음 거절 |
| D01 | v1 DB 백업/이관/실패 rollback 및 새 종류별 식별키/메모/삭제복원 |

자동시험은 URL 정책·저장·마이그레이션·네이티브 메시지 계약·DDE 합성 서버까지 검증한다. 실제 확장 설치, Office/뷰어 실기, 환경별 동작은 별도 증거로 기록한다. 결과는 docs/extension-validation.md를 기준으로 한다.

## API 근거

- Chrome Native Messaging: https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging
- Chrome activeTab: https://developer.chrome.com/docs/extensions/develop/concepts/activeTab
- Edge Native Messaging: https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging
- Word 선택 위치: https://learn.microsoft.com/en-us/office/vba/api/word.selection.start
- PowerPoint SlideID: https://learn.microsoft.com/en-us/office/vba/api/powerpoint.slide.slideid
- Sumatra DDE: https://www.sumatrapdfreader.org/docs/DDE-Commands
