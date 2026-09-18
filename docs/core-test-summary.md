# Core / SQLite 자동시험 결과

실행 환경: Windows, .NET SDK 10.0.401, net10.0, Microsoft.Data.Sqlite 10.0.12. 실제 실행 시각과 원문은 [JSON 증거](evidence/core-storage-tests.json), [표준 출력](evidence/core-storage-tests.txt)에 보관한다.

13개 시험 그룹 통과, 0개 실패. 아래 결과는 정책과 실제 로컬 SQLite 동작의 자동시험이며 탐색기/Excel 연결, Windows UI, 실제 업무 파일에 대한 전체 수용시험 통과를 뜻하지 않는다.

| 수용시험 ID | 자동시험으로 확인한 범위 | 결과 |
|---|---|---|
| E06, X12 | 한글·공백·특수문자·대소문자/Unicode 보존, 절대 Windows/UNC 경로, 장치/URI/ADS/예약 이름 거절 | 통과 |
| E07 | 문서 허용 목록, 실행/스크립트/바로가기/미등록 확장자 위치 표시 정책 | 통과 |
| X01, X12 | 절대 A1 단일 셀, XFD1048576 경계, 잘못된 시트/주소/확장자/상태 DTO 거절 | 통과 |
| U05, U06, U07 | 동일 키 ID 유지, 캡처와 독립된 메모, 메모 시각 보존, 셀/파일/대소문자 변형 구분 | 통과 |
| U08 | 시계 역행에도 캡처 순서 증가, 메모/재개에서 순서 불변, 재시작 영속성 | 통과 |
| U10 | 삭제 표식, 복구, 재캡처 복원, 메모/ID 유지, 실제 합성 원본 파일 불변 | 통과 |
| U13 | 1,026개 합성 기록, 최근 20개/검색 100개 제한, 전체 기록 검색, % 문자 리터럴 검색 | 통과 |
| D01, D02 | capture/note/delete/restore/resume/relink 커밋 직전 예외 주입, 이전 값/순서/행 보존 | 통과 |
| E09 | 경로만 수정, 시트/셀 변경 거절, 기존 키 충돌 시 전체 원본 보존 | 통과 |
| D03 | 손상 바이트, 알 수 없는 스키마, 미래 스키마의 원본 해시 보존 | 통과 |
| D03 | 기존 빈 v0 SQLite에 일관된 BackupDatabase 백업, 실패한 스키마 트랜잭션 롤백, 재시도 | 통과 |
| D01 | 동시 20개 요청 직렬화와 중복 없는 연속 sequence | 통과 |
| D01, D02 | 별도 SQLite 연결의 실제 writer lock, 제한시간 오류, 미커밋 행/순서 없음, 해제 후 복구 | 통과 |

최종 실행의 최근 목록 저장소 쿼리 단일 측정은 2.73 ms, SQLite 실제 writer lock 거절은 3.28초였다. UI 목록 p95나 외부 COM 성능 수치가 아니므로 제품 성능 목표 달성 근거로 사용하지 않는다.

재현:

~~~powershell
dotnet restore tests/WorkBookmark.Core.Tests/WorkBookmark.Core.Tests.csproj --configfile NuGet.Config --locked-mode
dotnet run --project tests/WorkBookmark.Core.Tests/WorkBookmark.Core.Tests.csproj -c Release --no-restore
~~~

시험은 실행마다 OS 임시 폴더 아래 고유 이름의 작업 폴더를 만들고 자기 합성 DB/파일만 삭제한다. 업무 문서·사용자 앱 DB를 사용하지 않는다.

## 저장소 구현의 경계

- 기본 SQLite rollback journal과 FULL synchronous를 사용하고, 모든 수정은 명시적 트랜잭션으로 커밋 후 반환한다. 요청별 연결을 사용하며 최대 3초 busy timeout을 둔다.
- 기존 DB 스키마 변경 전에 SQLite BackupDatabase로 백업한다. 기존 손상/불명확/미래 DB는 초기화하거나 덮어쓰지 않는다.
- v1의 첫 스키마만 제공한다. 알 수 없는 사용자 테이블이 있는 v0 파일을 기존 제품 DB라고 추측해 변환하지 않는다.
- 경로 정책은 파일/네트워크 접근이 없는 순수 검사다. 파일 존재·접근권한·Excel 시트/셀 존재 확인은 별도 worker의 책임이다. Relink 입력은 worker 검증을 거친 같은 종류/시트/셀 DTO여야 한다.
- Windows 장치 별칭, 후행 공백/점, UNC pipe/mailslot, 루트 바깥으로 나가는 .. 경로는 추측 없이 거절한다. 긴 경로/UNC/동기화 경로 실제 호환성은 이 자동시험으로 주장하지 않는다.

Microsoft.Data.Sqlite 10.0.0의 기본 의존성 SQLitePCLRaw 2.1.11은 NuGet 감사에서 알려진 취약점으로 차단되어 10.0.12로 고정했다. [10.0.12 NuGet 의존성](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12), [확인된 보안 공지](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
