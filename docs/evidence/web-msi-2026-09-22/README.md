# 0.1.5 자동검사 기록

`scripts/build.ps1 -ArtifactsPath artifacts/verification-015` 실행 결과를 `build-and-tests.log`에 보관합니다. 잠금 파일 복원과 전체 Release 빌드에 성공했으며 경고·오류는 0개입니다.

| 검사 | 통과 |
|---|---:|
| Core/Storage + 확장 정책 + 스냅샷 + Office URL | 44 |
| IPC | 16 |
| Desktop/입력/설치 종료 | 62 |
| Explorer/Shell | 10 |
| Excel 증거 판정 / 캡처 세션 판정 | 13 / 14 |
| Word 좌표 | 24 |
| Office URL / 확장 없는 브라우저 | 55 / 30 |
| 메모장 / PDF | 28 / 28 |
| BrowserHost / 실제 SQLite 통합 | 27 / 15 |
| 선택적 브라우저 확장 / 등록 경계 | 23 / 7 |
| 합계 | 396 |

최초 실행에서 실패한 검증 프로세스가 기존 테스트 출력 DLL을 점유했고, 이후 새 출력의 최초 실행도 일시적으로 접근 거부를 반환했습니다. 권한이나 보안 설정을 변경하지 않았습니다. SDK의 별도 출력 폴더를 사용하고 동일 검사 명령을 다시 실행하여 위의 전체 성공 결과를 얻었습니다. 실패 시도 로그는 로컬 `artifacts/testing/web-msi-0.1.5`에 남깁니다.

실제 AD/Office 문서, 회사 Edge/Chrome 접근성, MSI 설치·제거·재부팅 시험은 수행하지 않았습니다. MSI 구조와 관리 이미지 추출 결과는 배포 파일 옆 `.validation.json`에서 확인합니다.
