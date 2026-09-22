# Windows MSI 설치 및 운영

일반 사용자의 설치·사용·백업·문제 해결은 **[설치 및 사용 매뉴얼](user-manual.ko.md)**에서 한 번에 확인할 수 있습니다. 이 문서는 MSI 운영·빌드·검증의 상세 안내입니다.

`WorkBookmark-0.1.6-win-x64.msi`는 Windows x64용 사용자별 설치 패키지입니다. .NET 런타임을 포함하므로 별도의 .NET 설치가 필요하지 않습니다. 파일은 `%LOCALAPPDATA%\Programs\WorkBookmark`에 설치하며, 관리자 권한을 요청하지 않습니다. 조직의 Windows Installer 실행 정책은 별도로 적용됩니다.

## 설치, 실행, 재부팅

1. MSI를 실행합니다. 이전 MSI 설치가 있으면 자동 제거한 뒤 새 버전을 설치합니다. 설치 완료 화면의 **업무 책갈피 실행**은 기본 선택되어 있습니다. 바로 실행하려면 선택한 채 마치고, 나중에 실행하려면 체크를 해제합니다. 무인 설치(`/qn`)에서는 앱을 자동 실행하지 않습니다.
2. 시작 메뉴의 **WorkBookmark → WorkBookmark**로 다시 실행할 수 있습니다.
3. 최초 설치에서는 현재 사용자의 Windows 로그인 시 앱이 자동 실행됩니다. 설정의 **Windows 로그인 시 실행**을 끄면 자동 실행을 중지합니다. Windows에서 로그아웃하거나 재부팅한 후 로그인하면 이 선택에 따라 실행됩니다.
4. 책갈피, 메모, 설정은 `%LOCALAPPDATA%\WorkBookmark`에 보관됩니다. 기존 ZIP판에서 사용하던 데이터도 같은 위치이므로 이어서 사용합니다.

이미 실행 중인 ZIP판이 있으면 설치 전에 트레이 메뉴에서 종료하세요. 같은 사용자 세션에서는 앱을 하나만 실행합니다. 앱의 새 설치 경로는 시작 메뉴 바로가기에서 확인할 수 있습니다.

자동 실행 설정을 한 번 변경하면 그 선택은 `HKCU\Software\WorkBookmark\Preferences`에도 기록됩니다. 이후 MSI 업그레이드나 재설치 때 **끄기** 선택을 존중합니다. 자동 실행을 다시 켜려면 앱 설정을 사용하세요. Windows 작업 관리자에서 별도로 시작 앱을 비활성화한 경우에는 그 설정도 확인해야 합니다.

완료 화면의 **업무 책갈피 실행**은 설치 직후 한 번 실행할지를 정합니다. Windows 로그인 시 자동 실행 설정은 바꾸지 않습니다.

## 업그레이드와 제거

더 높은 버전의 MSI를 실행하면 같은 사용자의 이전 MSI 설치를 자동으로 찾아 제거한 뒤 새 버전을 설치합니다. 사용자가 먼저 제어판에서 제거할 필요는 없습니다. 교체와 제거 전에 앱에 정상 종료를 요청합니다. 책갈피·메모·설정과 로그인 자동 실행 선택은 보존합니다. 포터블 ZIP 폴더는 Windows Installer에 등록된 설치가 아니므로 자동 제거 대상에 포함하지 않습니다. 낮은 버전으로의 덮어쓰기는 차단하며, 같은 버전의 실행은 Windows Installer 유지 관리 동작을 따릅니다. MSI를 다시 배포할 때에는 세 자리 제품 버전을 올려야 합니다.

**설정 → 앱 → 설치된 앱 → WorkBookmark → 제거**, 또는 **제어판 → 프로그램 및 기능 → WorkBookmark → 제거**를 이용합니다. 실행 파일, 시작 메뉴 및 자동 실행 바로가기를 제거합니다. 책갈피와 설정 데이터는 보존하므로 재설치 후 이어서 사용할 수 있습니다. 데이터까지 삭제하려면 앱 제거 후 `%LOCALAPPDATA%\WorkBookmark`를 사용자가 별도로 삭제합니다.

## 빌드

Windows에서 저장소 SDK 및 Node를 사용합니다.

```powershell
# 전체 자동검사 후 self-contained 게시와 MSI 생성 (작업 중인 변경도 가능)
./scripts/build.ps1 -Msi

# 별도 obj/bin에서 검증/게시 (기존 실행 파일의 잠금과 병렬 작업 충돌 방지)
./scripts/build.ps1 -Msi -ArtifactsPath ./artifacts/verification-016

# 커밋 후 ZIP + 커밋 소스 ZIP + MSI + 검증 JSON + SHA256SUMS 생성
./scripts/build.ps1 -Package -Msi

# 커밋 전 수정과 새 파일까지 포함한 현재 작업 트리로 배포 패키지 생성
./scripts/build.ps1 -Package -Msi -SourceMode WorkingTree -ArtifactsPath ./artifacts/verification-016

# 이미 게시한 디렉터리에서 MSI만 생성
./scripts/build-msi.ps1 -PublishDirectory ./artifacts/publish/<게시폴더>

# 설치 없이 관리 이미지 추출 및 모든 파일 해시 검증
./scripts/test-msi.ps1 -MsiPath ./artifacts/installer/WorkBookmark-0.1.6-win-x64.msi `
    -PublishDirectory ./artifacts/publish/<게시폴더> -Extract
```

빌드에만 [WiX 4.0.6](https://www.nuget.org/packages/wix/4.0.6)을 사용하며, 저장소의 `.tools/wix`에 정확한 버전을 설치합니다. WiX 자체 실행 파일은 제품에 포함하지 않습니다. 버전 고정의 근거는 해당 릴리스의 MS-RL 조건과 재현 가능한 빌드입니다. [WiX 4.0.6 소스/라이선스](https://github.com/wixtoolset/wix/tree/v4.0.6)를 참고하세요. WiX 빌드 도구는 저장소 SDK 런타임에서 major roll-forward로 실행하고, 제품은 자체 런타임을 사용합니다.

소스 ZIP은 기본 `Commit` 모드에서 깨끗한 작업 트리의 Git HEAD를 사용합니다. `-SourceMode WorkingTree`를 명시하면 현재 수정 사항과 새 소스 파일을 포함합니다. 기준 커밋과 소스 모드는 `SourceSnapshot.json`에 기록합니다. 이 옵션은 소스 스냅샷을 만들며 Git 커밋이나 GitHub 릴리스를 생성하지 않습니다.

`installer/Package.wxs`의 UpgradeCode는 버전 간 유지합니다. 게시 파일별 component GUID와 ID는 버전 또는 빌드 경로와 무관하게 상대 경로에서 결정합니다. 설치 디렉터리와 데이터 디렉터리를 분리하며, 사용자의 데이터나 인증 정보를 설치 파일에 넣지 않습니다. MSI 검증을 생략하는 빌드 옵션은 사용하지 않습니다.

## 검증 범위

자동 검증은 MSI의 사용자 범위, 관리자 권한 요구 없음, x64 플랫폼, 제품 버전, 제어판 제거 등록, 설치/바로가기 경로, 자동 실행 선택 보존 조건, 업그레이드/종료 순서, 완료 화면의 실행 선택 조건, 데이터 삭제 동작 없음, 파일 목록을 검사합니다. `-Extract`는 `msiexec /a`로 관리 이미지만 만들고 게시 파일 전체의 SHA-256을 대조합니다. 실제 사용자에게 설치하거나 기존 앱을 제거하지 않습니다.

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
