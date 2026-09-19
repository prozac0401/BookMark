# 확장 구현 검증 기록 — 2026-09-19

요구 출처와 결정은 [확장 요구명세](extension-requirements.md), 설치는 [브라우저 안내](../browser-extension/README.md)를 따른다. 이 변경은 소스와 로컬 시험판이다. 기존 v1 필수 실기의 미실행 항목을 통과로 바꾸지 않는다.

## 구현한 내용

- Word 본문 문자 위치, PowerPoint SlideID, PDF 물리 페이지, WebPage URL의 DTO·저장·표시·재개 분기.
- v1 DB의 일관된 사전 백업과 v2 트랜잭션 이관. 기존 메모·시각·재개/삭제 상태·capture_sequence 보존.
- Edge/Chrome 공통 Manifest V3 확장, Native Messaging 별도 EXE, 사용자·세션별 로컬 파이프, 앱의 DB 커밋 뒤 성공 응답.
- 사용자별 호스트 등록·해제와 -WhatIf, 실제 확장 ID 허용 목록, 타 설치 보호. 등록은 실행하지 않았다.
- 문서/뷰어 연결 실패·불명확한 선택·없는 좌표는 성공으로 추측하지 않는다. 원본 문서 저장/닫기/종료를 호출하지 않는다.

## 자동 검사 결과

두 번째 전체 빌드: 경고 0, 오류 0. 아래 결과의 원문은 [자동 검사 증거](evidence/extensions-2026-09-19/automatic-second-pass.json)에 보존했다. 최종 수정 후 [검사 원문](evidence/extensions-2026-09-19/final-verification.json)에서 총 192개 자동 검사(묶음 포함)를 확인했다. 이전에 통과한 변경 없는 Core/호스트 검사는 해당 빌드 결과를 유지하고, 변경된 Desktop/IPC/PDF/확장은 다시 실행했다.

| 범위 | 확인 결과 | 한계 |
|---|---|---|
| 기존 Core/SQLite | 13개 통과 | 실제 탐색기/Office UI 별도 |
| 새 종류/URL/이관 | 8개 묶음 통과 | 합성 DB와 메타데이터 |
| Desktop | 28개 통과(브라우저 저장경계 6개 포함) | 저장소 실패/지연 주입; 실제 한국어 IME 별도 |
| BrowserHost | 27개 통과 | 실제 EXE·프레임·파이프, 앱 상대는 mock |
| 브라우저 확장 | 16개 통과 | Chrome API mock; 브라우저 설치 실기 별도 |
| Word 좌표 | 16개 통과 | 아래 실기 결과 참고 |
| IPC | 12개 통과 | 실제 worker 프로세스와 실패 주입 |
| PDF | 28개 통과 | 별도 STA 합성 DDE 서버, 실제 Sumatra 아님 |
| Shell 경계 / Series 증거 / Session 판정 | 10 / 13 / 14개 통과 | 기존 자동 회귀시험 |
| 등록 소유권 | 7개 통과 | 메모리 레지스트리 대역; 실제 설정 변경 없음 |

## 발견한 문제와 조치

- 첫 IPC 검사에서 worker PID marker가 작성 중일 때 읽는 시험 준비 경쟁이 발생했다. marker를 임시 파일에 쓰고 닫은 뒤 최종 이름으로 이동하도록 보강했다. worker 종료 확인과 기존 기한은 그대로 유지한다. 수정 후 IPC 12개 전부 통과했다.
- 첫 PDF 합성 통신 검사는 26개 확인 뒤 종료되지 않았다. 해당 pdf-checks 프로세스만 명령행·PID 확인 후 종료했다. 파서/통신 통과를 전체 통과로 계산하지 않았다. 합성 DDE 서버를 각각 별도 STA로 옮기고, 없는 HWND를 연결 전에 거절하며, 시험에 8초 watchdog을 추가했다. 수정 후 28개 전부 통과했으며 종료 지연을 재현하지 않았다. DDE connect/disconnect 자체는 native 기한 인자를 받지 않으므로 제품에서는 기존 격리 worker의 전체 제한시간이 최종 경계다.
- PowerShell 안에서 native EXE 호출이 이 실행 환경에서 출력·종료코드를 반환하지 않는 현상은 여전히 원인 미확정이다. PowerShell 자체의 .NET/레지스트리 읽기는 가능하다. 이번 .NET/Node 빌드·시험은 구조화된 인수로 EXE를 직접 실행했다. 실행 정책 때문이라고 단정하거나 정책을 변경하지 않았다.

- 최종 코드 검토에서 DB 커밋 후 알림창 오류가 저장 실패 응답으로 바뀔 가능성을 수정했다. 성공 응답은 커밋 직후 확정하고 알림 실패와 분리했다. 수정 후 Desktop 28개를 다시 통과했다.

## 설치된 제품 실기 상태

- Edge·Chrome: 확장 로드, 실제 확장 ID 등록, 버튼/단축키부터 DB·재개까지의 E2E는 미실행. 사용자의 브라우저 설정/레지스트리를 자동 변경하지 않았다. 등록 스크립트와 설치 절차를 제공한다.
- Word·PowerPoint: 합성 문서 전용 native 하네스와 fixture를 추가했다. [Word 시도](evidence/extensions-2026-09-19/word-native.json)는 전경 활성화 거절로 차단됐다. [PPT 첫 시도](evidence/extensions-2026-09-19/powerpoint-native.json)는 paneClassDC가 없어서 실패했다. [읽기 전용 진단](evidence/extensions-2026-09-19/powerpoint-native-inventory.json)에서 실제 문서는 로드되어 있었고 mdiClass NativeOM이 해당 문서/활성 창과 일치함을 확인했다. 관측된 mdiClass는 paneClassDC가 없는 경우에만 사용하는 경로로 반영했고 기존 프로세스/창/문서 동일성 검증을 유지했다. [PPT 재시도](evidence/extensions-2026-09-19/powerpoint-native-retry.json)는 이 연결 단계 뒤 전경 활성화 거절로 차단됐다. 이 결과를 완전한 캡처·재개 통과로 표시하지 않는다. 1개 창의 성공이 다중 창·문서 재편집·보호 보기·32비트 Office 전체 지원을 뜻하지 않는다.
- PDF: 현재 기본 연결 Adobe Acrobat 64-bit 26.002.21901. Sumatra 설치는 발견하지 못했다. Adobe 및 Edge PDF 페이지 캡처는 미지원. SumatraPDF 3.7+의 실제 DDE 기능 응답을 요구하는 실험 구현이며 설치된 뷰어 실기는 미실행이다.
- 기존 Excel 50회 연속·탐색기 활성 탭·DRM/UNC·한국어 IME 등 이전의 필수 실기 잔여는 [기존 검증 보고서](validation-report.md)를 그대로 따른다.

## 후속 확인

B01~B06, W01, P01, F01의 제품별 실기를 합성 자료로 진행해야 한다. Chrome과 Edge 각각 설치→일반 탭 저장→메모 유지→목록 Enter 재개, 다중 창·탭 전환, 앱 미실행/DB실패, 금지 scheme, 해제 후 오류를 확인하고 OS/브라우저/Office 버전·결과를 남긴다. 스토어 게시, 자동 업데이트, 다른 Chromium 제품, PDF 브라우저 페이지 번호는 아직 구현·인증 범위가 아니다.
