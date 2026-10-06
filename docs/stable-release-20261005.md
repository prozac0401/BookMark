# WorkBookmark 0.2.8 정식 릴리스 채널 전환

- 날짜: 2026-10-05
- 사용자 요구: 특별한 문제가 없다면 업무 책갈피를 정식 배포. 실제 Whale 저장·재열기의 정상 동작은 사용자가 확인했습니다.
- 배포: [기존 v0.2.8](https://github.com/prozac0401/BookMark/releases/tag/v0.2.8)의 prerelease 표시를 해제했습니다. 태그·소스·MSI/ZIP·모든 첨부 파일을 보존했습니다.

이번 변경은 안내와 배포 채널에만 적용합니다. 기존 0.2.8 사용자는 다시 설치할 필요가 없습니다. runtime·설치 방식·DB v5·설정·사용자 표시 모드·크기·설치 경로 보존 규칙을 변경하지 않았습니다.

## 파일과 소스

소스는 기존 tag/SourceSnapshot.json의 `d507a885ed5f73bca95ee0989bb2e0295915a787`입니다. 후속 안내 변경은 첨부 Source.zip이나 기존 패키지를 다시 만들었다는 의미가 아닙니다.

| 파일 | SHA-256 |
|---|---|
| WorkBookmark-0.2.8-win-x64.msi | `4e2dba00c42e7b169d0a88cb9b43d70ded960dc1ba1b50176624d903295a4ba0` |
| WorkBookmark-0.2.8-win-x64.zip | `643a496c6ac21f0b60685d45bfd68b35a3bcce3a0e34adeefb612fafbd035c78` |
| WorkBookmark-0.2.8-win-x64.validation.json | `6e49df91a961663b1a9cc90aa6dac26b8f322d6e62b0b6eaca43eea35df55fb8` |
| SourceSnapshot.json | `7db1141407f57e8c6d9ffbf11d3399b237d95dd326c94f8cc6d5c072431f655f` |

실제 내려받은 MSI·검증 JSON·SourceSnapshot·SHA256SUMS.txt를 대조했습니다. ZIP은 기존 GitHub 자산 digest·SHA256SUMS 기록과 연결하며 자산을 교체하지 않았습니다. ProductCode `{CCF6B586-B804-EE43-6E8F-2FBAB720368B}`, PackageCode `{A2B412BC-FA3C-41CA-B037-EEA299F2D846}`, UpgradeCode `{BB5C5CA8-5BA0-4B01-875E-9E295690C711}`는 실제 MSI를 읽기 전용으로 확인했습니다.

## 검증 판정

| 근거 | 판정·범위 |
|---|---|
| 기존 0.2.8 빌드·브라우저 자동검사 | 경고/오류 0·34 PASS 재사용. 이번 재실행 아님 |
| 기존 최종 패키지 검사 | 54 PASS 재사용. 구조/파일 추출 검사이며 실제 설치 수명주기 PASS 아님 |
| 실제 Whale 저장·재열기 | USER_CONFIRMED. 사용자의 정상 동작 보고. 정확한 build·원시 로그 없는 통제되지 않은 확인 |
| 최종 0.2.8 MSI 실제 설치·제거·재부팅 | NOT RUN 유지. 이전 버전 성공을 소급 합산하지 않음 |
| 전체 Office·스티커·IME·배율·여러 모니터·회사 인증·Office 32비트/UNC/OneDrive | 기존 미검증 범위 유지. 이번 채널 전환에서 실행하지 않음 |

[2026-09-27 제작·검증 이력](release-notes-v0.2.8.md)과 [환경별 상태](compatibility.md)는 당시 결과로 보존합니다. Whale 사용자 확인을 모든 탭/프로필·인증·Whale 확장 Native Messaging PASS로 확대하지 않습니다. 코드 서명·회사별 도입 승인·전체 인수는 정식 채널 게시와 구분합니다. [기존 설치 경로 이행](windows-installer.ko.md)과 [백업](user-manual.ko.md) 안내는 계속 적용됩니다. 사용자 설치·실제 책갈피 데이터·Office 문서를 변경하지 않았습니다.

## 게시 후 확인

GitHub v0.2.8은 draft·prerelease가 아니며 기존 자산 6개의 ID·크기·digest가 모두 보존돼 있습니다. MSI를 새로 빌드하거나 태그를 옮기지 않았습니다. 현재 사용 문서에서 정식 채널과 사용자 Whale 확인을 반영하고 추가한 상대 링크를 확인했습니다. 공개 Release 본문에는 2026-09-27 원문을 역사적 기록으로 보존했습니다.
