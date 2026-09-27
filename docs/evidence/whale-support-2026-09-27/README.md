# 0.2.8 웨일 무확장 저장 경로 검증 기록

2026-09-27 · Windows x64

네이버 웨일의 일반 브라우저 창을 기존 UI Automation 캡처 경로에 추가했습니다. 허용 프로세스는 msedge·chrome·whale이며 이름을 대소문자 구분 없이 정확히 비교합니다. 기존 창 클래스 검사, 비관리자 프로세스 제한, 주소창·최상위 문서의 URL 일치, 두 차례 안정성 확인, HTTP(S) 제한을 유지합니다.

| 확인 범위 | 결과·근거 |
|---|---|
| App·Windows·WindowsChecks Release 빌드 | 경고 0개·오류 0개 · [release-build.log](release-build.log) |
| 브라우저 프로세스·URL·관찰 안정성 | browser-checks **34개 통과** · [browser-checks.log](browser-checks.log) |
| 실제 웨일 창의 UIA 주소·제목 저장 및 재열기 | 미검증 |
| 패키지 구조·추출 파일·소스·해시 | 소스 커밋 후 기존 패키징을 한 차례 실행하며 결과는 릴리스 첨부 검증 JSON으로 제공 |

검사는 합성 브라우저 메타데이터로 수행했습니다. 웨일의 프로세스 허용, 유사 프로세스 이름 거절, whale://settings·whale://newtab 거절을 추가했으며 기존 URL 일치·주소 입력 중 거절·탭 및 창 변경·기한·제목 정규화 검사를 유지합니다. 실제 웨일 버전의 창 클래스, 주소창 식별, Document ValuePattern과 사용자 환경의 캡처 성공을 이 결과로 보장하지 않습니다.

검사 실행 명령은 dotnet tools/WindowsChecks/bin/Release/net10.0-windows/WindowsChecks.dll browser-checks입니다. dotnet run의 apphost 시작은 OS 액세스 거부로 실패하여 동일 빌드 DLL을 dotnet으로 실행했습니다. 최초 시작 실패는 [apphost-launch.log](apphost-launch.log)에 남겼으며 애플리케이션 동작 실패나 검사 통과로 집계하지 않았습니다.

바로가기는 기존 ShellExecute URL 경로를 유지하므로 Windows 기본 브라우저로 열립니다. 웨일 확장 프로그램의 Native Messaging 등록은 이번 변경 범위에 포함하지 않습니다. 스티커·Office·IPC·확장 프로그램·전체 통합 검사를 반복하지 않았고 사용자 브라우저나 컴퓨터 입력을 조작하지 않았습니다.

[릴리스 안내](../../release-notes-v0.2.8.md)의 기존 6종 배포 형식을 사용합니다. 패키지 검사는 UIA 실기 검증을 대신하지 않습니다.
