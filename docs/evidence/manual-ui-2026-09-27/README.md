# 합성 저장소를 사용하는 실제 제품 화면 준비

기록일: 2026-09-27 · 관련 백로그: Workspace T08 · 상태: 시작·표시·정상 종료 확인, 실제 입력은 미실행

기존 사용자 DB를 수정하지 않고 실기를 재현하기 위해 Desktop.Tests에 `--manual-ui <fixture> <published-exe>` 진입점을 추가했다. 실제 `BookmarkApplicationContext`, SQLite 저장소, 게시한 실행 파일의 worker를 사용한다. 호스트의 제품 DLL과 게시 DLL의 SHA-256이 다르면 시작을 거절한다. 입력 전송·비공개 UI 이벤트 호출·IME 변경 기능은 없다.

## 확인한 범위

- 컴파일 경고·오류0. UI 호스트에 0.2.4 후보의 관리 DLL 네 개를 동일 바이트로 복사했다. `WorkBookmark.dll` SHA-256은 `b4e379f54974684a1486455a3c6f902d8c840f4dcaa5e042c3bf79b3e38f25a5`다.
- 별도 합성 파일·폴더 책갈피 두 개와 스티커가 표시됐다. 스티커는 다른 창 위에 표시됐고 저장소에서도 두 layout의 AlwaysOnTop이 true로 보존됐다.
- 제어 창의 **저장소 스냅샷 기록** 버튼은 실제 클릭으로 동작했다. 합성 대상 파일의 원래 해시가 유지됐다.
- 제어 창의 **정상 종료** 버튼으로 종료 코드0, 프로세스 종료, 원래 사용자 DB·설정 파일의 존재 및 SHA-256 불변을 확인했다.

스티커는 화면 캡처에 보였지만 현재 UI 제어 도구의 창·앱 목록에는 제어 창만 반환됐다. 제품 보기 단축키 이후 재조회해도 스티커를 선택할 수 없었다. 따라서 실제 한글 IME, 창 전환 자동 저장, 실패 초안 보존, 이동·접기·재시작 복원, 작업 재개의 입력 시험은 **NOT_RUN**이다. 이를 제품 실패로 확정하거나 기존 합성 IME 검사의 PASS로 대체하지 않는다.

일반 설치 앱의 `Program.Main`, 자동 시작, 재부팅, 실행 중 제거는 이 호스트의 검증 범위 밖이다. 호스트는 현재 사용자 앱 mutex를 사용하므로 기존 앱이 실행 중이면 시작을 거절한다. 저장소·진단·스냅샷은 checkout의 `artifacts/manual-ui` 하위에만 생성하고 기존 사용자 DB에는 쓰지 않는다. 실패 주입은 합성 저장소의 메모 트랜잭션만 거절한다.

## 재현

먼저 같은 소스의 `scripts/build.ps1 -Msi -ArtifactsPath ./artifacts/build`로 제품과 테스트를 빌드한다. UI 호스트의 제품 DLL은 반드시 확인할 publish 폴더의 파일로 맞춘다. 아래 변수는 해당 checkout의 실제 경로로 지정한다.

```powershell
$taskDotnet = (Resolve-Path './.tools/dotnet/dotnet.exe').Path
$env:DOTNET_ROOT = Split-Path -Parent $taskDotnet
$publish = (Resolve-Path './artifacts/publish/<검증할 게시 폴더>').Path
$output = (Resolve-Path './artifacts/build/bin/WorkBookmark.Desktop.Tests/release').Path
$fixture = [IO.Path]::GetFullPath('./artifacts/manual-ui/manual-session')
& $taskDotnet build tests/WorkBookmark.Desktop.Tests -c Release --no-restore --artifacts-path ./artifacts/build -p:BuildProjectReferences=false
if ($LASTEXITCODE -ne 0) { throw 'UI host build failed.' }
foreach ($name in @('WorkBookmark.dll','WorkBookmark.Core.dll','WorkBookmark.Storage.dll','WorkBookmark.Windows.dll')) {
    Copy-Item -LiteralPath (Join-Path $publish $name) -Destination (Join-Path $output $name) -Force
}
& "$output/WorkBookmark.Desktop.Tests.exe" --manual-ui $fixture "$publish/WorkBookmark.exe"
```

fixture는 자동 생성되며, 같은 경로를 다시 사용하면 동일 책갈피·메모·배치를 연다. 소유 marker가 없는 기존 폴더는 거절한다. 호스트를 종료한 다음 다른 설치·Excel 시험을 시작한다.

사람이 직접 입력하거나 스티커를 선택할 수 있는 UI 도구에서 다음을 확인한다.

1. 첫 스티커 메모에 한국어 키보드로 입력하고 조합 확정 Enter가 의도치 않은 제출을 만들지 않는지 확인한다. 제어 창 입력칸으로 이동한 뒤 최종 메모가 저장되는지 확인한다.
2. 제어 창의 실패 주입을 먼저 켜고 다른 초안을 입력한다. 창 전환 후 실패 안내·편집 가능한 초안·이동한 창의 포커스와 기존 저장 값을 확인한다. 주입을 끄고 다시 시도해 보존된 초안을 저장한다.
3. 스티커를 이동·접고 정상 종료한다. 같은 fixture로 다시 실행해 메모와 배치 복원을 확인한다.
4. 합성 폴더의 **이어가기**를 눌러 정확한 폴더가 열리는지 확인하고 원본 해시를 비교한다.

스냅샷은 고정된 두 합성 책갈피만 기록하며 DB 커밋 결과와 화면 입력 결과를 대조하는 자료다. 초기 noteAttempts에는 fixture를 만드는 두 메모 쓰기도 포함되므로 단계 간 증가량으로 비교한다. 원시 스냅샷·진단·사용자 화면은 공개 저장소에 넣지 않는다.
