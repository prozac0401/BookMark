# Windows MSI 설치 및 운영

일반 사용자의 설치·사용·백업·문제 해결은 **[설치 및 사용 매뉴얼](user-manual.ko.md)**에서 한 번에 확인할 수 있습니다. 이 문서는 MSI 운영·빌드·검증의 상세 안내입니다.

`WorkBookmark-0.2.4-win-x64.msi`는 Windows x64용 사용자별 설치 패키지입니다. .NET 런타임을 포함하므로 별도의 .NET 설치가 필요하지 않습니다. 파일은 `%LOCALAPPDATA%\Programs\WorkBookmark`에 설치하며, 관리자 권한을 요청하지 않습니다. 조직의 Windows Installer 실행 정책은 별도로 적용됩니다.

0.2.4는 사용자 지정 설치 경로의 복구·제거를 수정한 평가판입니다. [릴리스 안내](release-notes-v0.2.4.md)와 [설치 검증 기록](evidence/install-location-2026-09-27/README.md)을 참고하세요. 0.2.0~0.2.3과 같은 DB v5를 사용합니다.

**0.2.3 이하를 사용자 지정 경로에 설치했다면 기존 MSI와 원래 INSTALLFOLDER를 지정해 먼저 제거한 뒤 새로 설치하세요.** 기본 경로 설치는 일반 업데이트를 확인했습니다. [설치 경로 이행 안내](windows-installer.ko.md)를 먼저 확인하세요.

## 설치, 실행, 재부팅

1. MSI를 실행합니다. 이전 MSI 설치가 있으면 자동 제거한 뒤 새 버전을 설치합니다. 설치 완료 화면의 **업무 책갈피 실행**은 기본 선택되어 있습니다. 바로 실행하려면 선택한 채 마치고, 나중에 실행하려면 체크를 해제합니다. 무인 설치(`/qn`)에서는 앱을 자동 실행하지 않습니다.
2. 시작 메뉴의 **WorkBookmark → WorkBookmark**로 다시 실행할 수 있습니다.
3. 최초 설치에서는 현재 사용자의 Windows 로그인 시 앱이 자동 실행됩니다. 설정의 **Windows 로그인 시 실행**을 끄면 자동 실행을 중지합니다. Windows에서 로그아웃하거나 재부팅한 후 로그인하면 이 선택에 따라 실행됩니다.
4. 책갈피, 메모, 설정은 `%LOCALAPPDATA%\WorkBookmark`에 보관됩니다. 기존 ZIP판에서 사용하던 데이터도 같은 위치이므로 이어서 사용합니다.

이미 실행 중인 ZIP판이 있으면 설치 전에 트레이 메뉴에서 종료하세요. 같은 사용자 세션에서는 앱을 하나만 실행합니다. 앱의 새 설치 경로는 시작 메뉴 바로가기에서 확인할 수 있습니다.

자동 실행 설정을 한 번 변경하면 그 선택은 `HKCU\Software\WorkBookmark\Preferences`에도 기록됩니다. 이후 MSI 업그레이드나 재설치 때 **끄기** 선택을 존중합니다. 자동 실행을 다시 켜려면 앱 설정을 사용하세요. Windows 작업 관리자에서 별도로 시작 앱을 비활성화한 경우에는 그 설정도 확인해야 합니다.

완료 화면의 **업무 책갈피 실행**은 설치 직후 한 번 실행할지를 정합니다. Windows 로그인 시 자동 실행 설정은 바꾸지 않습니다.

## 업그레이드와 제거

더 높은 버전의 MSI를 실행하면 같은 사용자의 이전 MSI 설치를 자동으로 찾아 제거한 뒤 새 버전을 설치합니다. 기본 경로 설치는 먼저 제거할 필요가 없습니다. 0.2.3 이하 사용자 지정 경로는 아래 이행 절차를 따르세요. 교체와 제거 전에 앱에 정상 종료를 요청합니다. 책갈피·메모·설정과 로그인 자동 실행 선택은 보존합니다. 포터블 ZIP 폴더는 Windows Installer에 등록된 설치가 아니므로 자동 제거 대상에 포함하지 않습니다. 낮은 버전으로의 덮어쓰기는 차단하며, 같은 버전의 실행은 Windows Installer 유지 관리 동작을 따릅니다. MSI를 다시 배포할 때에는 세 자리 제품 버전을 올려야 합니다.

0.2.0 첫 실행은 기존 DB를 v5로 갱신하고 변경 전 `bookmarks.db.pre-migration-….bak`를 데이터 폴더에 남깁니다. **0.1.7은 갱신된 DB를 읽을 수 없습니다.** 구버전 복귀에는 현재 데이터 보존과 업그레이드 전 백업 복원이 필요하며, 복원한 백업 이후의 기록은 포함되지 않습니다. [백업·복원 안내](user-manual.ko.md#maintenance)를 확인하세요.

**설정 → 앱 → 설치된 앱 → WorkBookmark → 제거**, 또는 **제어판 → 프로그램 및 기능 → WorkBookmark → 제거**를 이용합니다. 실행 파일, 시작 메뉴 및 자동 실행 바로가기를 제거합니다. 책갈피와 설정 데이터는 보존하므로 재설치 후 이어서 사용할 수 있습니다. 데이터까지 삭제하려면 앱 제거 후 `%LOCALAPPDATA%\WorkBookmark`를 사용자가 별도로 삭제합니다.

## 0.2.4: 사용자 지정 설치 경로

0.2.4는 실제 설치 경로를 현재 사용자의 `HKCU\Software\WorkBookmark\Installer\InstallFolder`에 기록합니다. 복구·제거와 이후 업데이트는 이 경로를 다시 사용합니다. 유지 관리 명령에서 다른 `INSTALLFOLDER`를 전달해도 설치 위치를 옮기지 않습니다. 위치를 바꾸려면 먼저 제거한 뒤 원하는 경로에 새로 설치하세요. 검증한 동일 MSI를 평가판으로 공개했습니다.

새 설치에서 경로를 지정한 예입니다. 기본 설치 화면은 경로 선택 기능을 제공하지 않습니다.

```powershell
msiexec /i "WorkBookmark-0.2.4-win-x64.msi" INSTALLFOLDER="D:\Apps\WorkBookmark"
msiexec /fa "WorkBookmark-0.2.4-win-x64.msi"
msiexec /x "WorkBookmark-0.2.4-win-x64.msi"
```

**0.2.3 이하 사용자 지정 경로 설치는 먼저 기존 MSI에 원래 경로를 지정하여 제거하세요.** 이전 MSI는 설치 경로를 기록하지 않았으므로 일반 복구·제거가 기본 경로를 사용하고 원래 폴더에 파일을 남길 수 있습니다. 새 MSI도 과거 패키지의 제거 코드를 소급 변경하지 못합니다. 기본 경로의 기존 설치는 평소대로 업데이트할 수 있습니다.

```powershell
# 기존 설치 경로를 확인하고 앱을 정상 종료한 후 순서대로 실행
msiexec /x "WorkBookmark-0.2.3-win-x64.msi" INSTALLFOLDER="D:\Apps\WorkBookmark"
# 위 제거의 성공을 확인한 다음 새 버전을 설치
msiexec /i "WorkBookmark-0.2.4-win-x64.msi" INSTALLFOLDER="D:\Apps\WorkBookmark"
```

사용자 데이터 폴더는 위 경로와 별도이며 제거 시 보존합니다. 원래 경로가 불명확하거나 이미 복구 후 두 위치에 파일이 생긴 경우에는 기존 설치 로그·바로가기 대상을 먼저 확인하세요. 임의의 폴더를 일괄 삭제하지 마세요.

개발자는 `scripts/test-install-location.ps1`로 설치가 없는 일반 사용자 환경에서 후보 SHA-256을 고정하고 사용자 지정 경로의 복구·제거, 기본 경로 0.2.3 업데이트, 이전 사용자 지정 경로의 명시적 이전 절차를 순차 검증합니다. 기존 사용자 데이터는 내용 대신 해시로 보존 여부를 확인하며, 설치 파일 외의 시험용 파일도 남기는지 확인합니다. 시험용 후속 MSI를 사용하는 업데이트 검사와 118개 실기 체크포인트의 결과는 [0.2.4 설치 경로 검증 기록](evidence/install-location-2026-09-27/README.md)에 남겼습니다.

## 빌드

Windows에서 저장소 SDK 및 Node를 사용합니다.

```powershell
# 전체 자동검사 후 self-contained 게시와 MSI 생성 (작업 중인 변경도 가능)
./scripts/build.ps1 -Msi

# 별도 obj/bin에서 검증/게시 (기존 실행 파일의 잠금과 병렬 작업 충돌 방지)
./scripts/build.ps1 -Msi -ArtifactsPath ./artifacts/verification-023

# 커밋 후 ZIP + 커밋 소스 ZIP + MSI + 검증 JSON + SHA256SUMS 생성
./scripts/build.ps1 -Package -Msi

# 커밋 전 수정과 새 파일까지 포함한 현재 작업 트리로 배포 패키지 생성
./scripts/build.ps1 -Package -Msi -SourceMode WorkingTree -ArtifactsPath ./artifacts/verification-023

# 이미 게시한 디렉터리에서 MSI만 생성
./scripts/build-msi.ps1 -PublishDirectory ./artifacts/publish/<게시폴더>

# 설치 없이 관리 이미지 추출 및 모든 파일 해시 검증
./scripts/test-msi.ps1 -MsiPath ./artifacts/installer/WorkBookmark-0.2.4-win-x64.msi `
    -PublishDirectory ./artifacts/publish/<게시폴더> -Extract
```

빌드에만 [WiX 4.0.6](https://www.nuget.org/packages/wix/4.0.6)을 사용하며, 저장소의 `.tools/wix`에 정확한 버전을 설치합니다. WiX 자체 실행 파일은 제품에 포함하지 않습니다. 버전 고정의 근거는 해당 릴리스의 MS-RL 조건과 재현 가능한 빌드입니다. [WiX 4.0.6 소스/라이선스](https://github.com/wixtoolset/wix/tree/v4.0.6)를 참고하세요. WiX 빌드 도구는 저장소 SDK 런타임에서 major roll-forward로 실행하고, 제품은 자체 런타임을 사용합니다.

소스 ZIP은 기본 `Commit` 모드에서 깨끗한 작업 트리의 Git HEAD를 사용합니다. `-SourceMode WorkingTree`를 명시하면 현재 수정 사항과 새 소스 파일을 포함합니다. 기준 커밋과 소스 모드는 `SourceSnapshot.json`에 기록합니다. 이 옵션은 소스 스냅샷을 만들며 Git 커밋이나 GitHub 릴리스를 생성하지 않습니다.

`installer/Package.wxs`의 UpgradeCode는 버전 간 유지합니다. 게시 파일별 component GUID와 ID는 버전 또는 빌드 경로와 무관하게 상대 경로에서 결정합니다. 설치 디렉터리와 데이터 디렉터리를 분리하며, 사용자의 데이터나 인증 정보를 설치 파일에 넣지 않습니다. MSI 검증을 생략하는 빌드 옵션은 사용하지 않습니다.

## 검증 범위

자동 검증은 MSI의 사용자 범위, 관리자 권한 요구 없음, x64 플랫폼, 제품 버전, 제어판 제거 등록, 설치/바로가기 경로, 자동 실행 선택 보존 조건, 업그레이드/종료 순서, 완료 화면의 실행 선택 조건, 데이터 삭제 동작 없음, 파일 목록을 검사합니다. 설치 화면의 전용 이미지와 앱 아이콘은 MSI 내부 바이너리의 SHA-256까지 대조합니다. `-Extract`는 `msiexec /a`로 관리 이미지만 만들고 게시 파일 전체의 SHA-256을 대조합니다. 실제 사용자에게 설치하거나 기존 앱을 제거하지 않습니다.

실제 설치·제거·재부팅 동작은 별도 Windows 계정 또는 VM에서 다음 순서로 확인하세요.

1. 일반 사용자로 MSI 설치 → 완료 화면의 **업무 책갈피 실행**이 기본 선택인지 확인 → 선택한 채 마치기 → 앱/시작 메뉴/트레이 확인 → 책갈피 저장.
2. 로그아웃/로그인 또는 재부팅 → 앱 자동 실행 및 저장한 책갈피 복원 확인.
3. 자동 실행 끄기 → 재로그인 → 자동 실행되지 않음 확인.
4. 다음 버전 MSI 업그레이드 → 이전 버전이 자동 제거되고 새 버전만 등록되었는지 확인 → **업무 책갈피 실행** 체크 해제 후 마치기 → 앱이 바로 실행되지 않는지 확인 → 시작 메뉴로 실행해 책갈피와 자동 실행 끄기 선택 유지 확인.
5. 자동 실행 켜기 → 앱이 실행 중인 상태에서 제어판 제거 → 앱 종료 및 시작 메뉴/시작프로그램 바로가기 제거 확인.
6. 데이터 폴더 보존 확인 → 재설치 → 기존 책갈피 사용 확인.
7. 무인 설치(`/qn`) → 완료 후 앱이 자동 실행되지 않는지 확인.

조직 AD 인증, 브라우저 정책, Office 자동화, 실제 Windows 로그인/제거는 MSI 구조 검증만으로 보증되지 않습니다. 코드 서명 인증서를 제공받지 않았으므로 현재 MSI에는 Authenticode 서명이 없습니다.

설치 작성 참고: [사용자별 설치 범위](https://docs.firegiant.com/wix/schema/wxs/packagescopetype/), [MajorUpgrade](https://docs.firegiant.com/wix/schema/wxs/majorupgrade/), [바로가기](https://docs.firegiant.com/wix/schema/wxs/shortcut/), [제거할 파일](https://docs.firegiant.com/wix/schema/wxs/removefile/).
