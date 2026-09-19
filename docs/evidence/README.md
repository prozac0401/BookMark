# 공개 시험 증거

합성 시험 자료만 포함합니다. publish-initial.txt의 로컬 Windows 사용자명은 <user>로 치환했습니다. 사용자 프로필 경로가 보이는 구성요소 렌더 이미지는 공개본에서 제외했으며, 실제 목록 화면과 구성요소 시험 로그는 보존했습니다. 원문과 제외 이미지는 공개되지 않는 로컬 .artifacts/private-evidence에 보관합니다.

초기 실패와 최종 결과는 별도 파일로 구분합니다. 전체 판정은 ../validation-report.md를 따릅니다.

## 후속 구현 증거: continuation-2026-09-19

후속 작업의 새 빌드·84개 자동검증·publish 및 실행 환경, 최초 실패와 재검증을 별도 디렉터리에 보존합니다. 기존 preview.1 자료를 덮어쓰지 않았습니다. build-final.txt와 각 *-final.txt를 최신 판정으로 보세요. ipc-dll-driver-failed.txt는 잘못된 dotnet DLL 실행 방식의 실패이며 ipc.txt는 EXE(apphost)로 실행한 12/12 결과입니다. build-and-tests.txt는 PowerShell wrapper 실행 차단이므로 통과 증거가 아닙니다. series-startup-rejection.json은 잘못된 입력 거절 시험이며 실제 Excel 반복 시험이 아닙니다.
