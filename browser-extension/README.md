# WorkBookmark 브라우저 확장 시험판

Chrome와 Edge의 현재 일반 탭 주소·제목을 이 PC의 WorkBookmark 목록에 저장합니다. 브라우저 도구 모음의 확장 버튼을 누른 다음 **이 페이지 저장**을 누르거나, 브라우저에 포커스가 있을 때 **팝업에 표시되는 실제 저장 단축키**를 사용합니다. 새 설치의 기본 제안은 **Ctrl+Shift+9**이며, 기존 설정과 다른 확장의 충돌에 따라 배정되지 않을 수 있습니다. 저장할 때 WorkBookmark 앱이 실행 중이어야 합니다.

## 설치

이 폴더는 개발자 모드로 설치하는 Manifest V3 확장입니다. 아직 Chrome Web Store·Microsoft Edge Add-ons에 배포하지 않았습니다. 호스트 등록 스크립트를 작성했지만, 자동으로 확장을 설치하거나 레지스트리를 변경하지는 않았습니다.

1. 릴리스의 앱·브라우저 호스트 파일을 함께 유지합니다. `WorkBookmark.BrowserHost.exe`는 사용자 입력 없이 브라우저 메시지를 처리하는 별도 실행 파일입니다. 앱 실행 파일로 대체하면 안 됩니다.
2. Chrome의 `chrome://extensions` 또는 Edge의 `edge://extensions`에서 개발자 모드를 켜고 **압축해제된 확장 프로그램 로드**로 이 폴더를 선택합니다.
3. 각 브라우저에 표시된 실제 확장 ID를 복사합니다. 확장 ID는 브라우저별로 다를 수 있습니다. 아래 예시의 꺾쇠괄호 부분은 반드시 실제 값으로 바꿉니다.
4. PowerShell에서 필요한 브라우저만 등록합니다. 현재 사용자(HKCU)에게 적용하며 관리자 권한을 요구하지 않습니다.

```powershell
.\scripts\register-browser-host.ps1 -Browser Chrome -HostExecutable 'C:\WorkBookmark\browser-host\WorkBookmark.BrowserHost.exe' -ChromeExtensionId '<Chrome에 표시된 32자리 ID>'
.\scripts\register-browser-host.ps1 -Browser Edge -HostExecutable 'C:\WorkBookmark\browser-host\WorkBookmark.BrowserHost.exe' -EdgeExtensionId '<Edge에 표시된 32자리 ID>'
```

한 번에 둘을 등록하려면 `-Browser Both`와 두 ID를 모두 지정합니다. `-WhatIf`는 소유권·경로를 검사하고 변경을 실행하지 않습니다. 실행 정책으로 스크립트가 차단되면 조직의 정책을 확인하세요. 이 스크립트는 실행 정책을 바꾸지 않습니다.

호스트 manifest는 `%LOCALAPPDATA%\WorkBookmark\BrowserHosts`에 브라우저별로 생성합니다. `allowed_origins`에는 입력한 실제 확장 ID 하나만 등록합니다. 기존 타 설치의 등록·manifest를 덮어쓰지 않으며, 시스템 전체 등록을 새 사용자 등록으로 가리지 않습니다. 호스트 위치나 확장 ID가 바뀌면 같은 스크립트로 다시 등록할 수 있습니다.

등록 해제:

```powershell
.\scripts\unregister-browser-host.ps1 -Browser Both
```

해제는 이 스크립트의 소유 표시와 정확한 manifest 경로가 일치하는 HKCU 값·manifest만 제거합니다. 앱, 확장 설치, 저장된 책갈피는 남습니다. 다른 값이 남아 있는 레지스트리 키나 브라우저 상위 키는 삭제하지 않습니다.

## 저장이 되지 않을 때

- 브라우저에서는 확장의 **이 페이지 저장** 버튼 또는 팝업에 표시된 실제 저장 단축키로 저장합니다. 단축키가 미배정이면 팝업의 **단축키 설정 열기**를 눌러 **현재 페이지를 WorkBookmark에 저장**에 사용할 키를 지정합니다.
- 이전 기본키 **Ctrl+Shift+Y**는 Edge의 컬렉션 단축키와 겹칩니다. v0.2.2는 새 설치 기본 제안을 **Ctrl+Shift+9**로 바꿉니다. 업데이트가 기존 단축키나 미배정 상태를 자동으로 바꾼다고 보장하지 않으므로, 기존 설치에서는 실제 배정 상태를 확인해야 합니다. 사용자가 지정한 단축키는 유지합니다.
- 이전 시험판에서 “저장 실패. 이 화면의 작업 위치는 아직 지원하지 않습니다”가 나왔다면 앱 전역 저장 경로의 안내입니다. 최신 앱은 브라우저에서 Ctrl+Alt+B를 누를 때 브라우저용 저장 방법을 표시합니다. 확장의 버튼에서 실제 오류 안내를 확인해야 호스트 연결 문제인지 구분할 수 있습니다.
- 연결을 찾지 못했다는 안내는 브라우저 호스트 등록과 설치 경로를 확인하라는 뜻입니다.
- 확장 연결이 허용되지 않았다는 안내가 나오면 현재 브라우저의 실제 확장 ID와 등록된 ID를 확인합니다. 관리 정책으로 제한된 연결은 관리자에게 문의합니다.
- 연결 프로그램 실행 실패는 등록된 `browser-host/WorkBookmark.BrowserHost.exe` 파일이 있는지, 실행 가능한지 확인합니다.
- 연결이 끊기거나 응답이 오지 않으면 저장 결과를 확정할 수 없습니다. WorkBookmark 최근 목록에서 확인한 뒤 다시 저장합니다. 원본 주소·제목이나 원문 연결 오류를 진단 로그로 남기지 않습니다.

## 저장 범위와 결정

- 권한은 `activeTab`, `nativeMessaging` 두 개입니다. 사용자 동작으로 허용된 현재 탭의 URL·제목만 조회합니다. 전체 탭 권한, 상시 사이트 접근, 본문 스크립트, 방문 기록, 쿠키 접근, 원격 서버 전송은 없습니다.
- 주소는 유효한 http·https이고 사용자명·비밀번호를 포함하지 않아야 합니다. 브라우저 내부 페이지, 로컬 파일, javascript/data 주소, 비공개 창, 이동 중인 페이지는 거절합니다.
- URL의 쿼리와 fragment는 원문 그대로 저장합니다. 페이지가 같은 검색 조건이나 내부 위치로 열리는 데 필요하기 때문입니다. 공유·로그인 토큰이 주소에 있다면 로컬 책갈피 DB에도 포함됩니다.
- URL은 최대 16,384 UTF-16 단위, 제목은 최대 256 단위입니다. 제목은 잘리는 지점의 서로게이트 문자를 보존하도록 조정합니다.
- URL·탭·창을 두 번 확인하고 팝업에서 본 페이지와 다르면 저장하지 않습니다. 이후의 브라우저 이동이나 웹사이트 로그인 상태까지 보장하지는 않습니다.
- 호스트가 동일한 requestId에 `success:true`, `code:"CaptureCommitted"`로 응답한 경우에만 완료로 표시합니다. DB 저장 성공이 확인되기 전에는 성공 배지를 붙이지 않습니다.
- 응답 유실·15초 제한시간 초과는 결과 미확인입니다. 자동으로 다시 보내지 않으며, WorkBookmark 목록에서 저장 여부를 확인하도록 안내합니다.
- 현재 단계는 URL 책갈피입니다. 페이지 안 스크롤·선택 문장·폼 입력·웹앱 편집 상태는 저장하지 않습니다. 재개 시 앱은 저장된 주소를 기본 브라우저로 열기 요청합니다. 원래 브라우저·프로필·기존 탭 재사용은 보장하지 않습니다.
- 단축키가 충돌하면 `chrome://extensions/shortcuts` 또는 `edge://extensions/shortcuts`에서 바꿉니다. 팝업은 실제 배정된 단축키 또는 미배정 상태를 표시합니다.

## 호스트 계약

Native messaging 이름: `com.workbookmark.capture`

요청:

```json
{"protocolVersion":1,"action":"capture","requestId":"UUID","url":"https://example.com/?q=synthetic#part","title":"Synthetic page"}
```

응답:

```json
{"requestId":"요청과 동일한 UUID","success":true,"code":"CaptureCommitted","message":"저장했습니다."}
```

브라우저는 별도 `WorkBookmark.BrowserHost.exe`에 호출 확장 origin과 선택적인 `--parent-window=...`를 전달합니다. Native host manifest에는 사용자 정의 실행 인수를 지정하는 항목이 없습니다. 표준 입출력은 UTF-8 JSON에 4바이트 little-endian 길이를 붙인 native messaging 전용이며, 일반 로그를 stdout에 쓰면 안 됩니다.

## 검증

```text
node --test browser-extension/tests/*.test.mjs
```

자동 검증은 최소 권한 manifest, URL 보존·거절, 제목 경계, 탭 전환, DB commit 응답 대기, 중복 요청, 응답 유실·시간 초과를 가짜 브라우저 API로 확인합니다. Chrome·Edge의 실제 확장 설치/권한 부여/레지스트리/native host/DB/재개를 함께 통과했다는 뜻은 아닙니다.

실제 실기에는 임시 페이지·시험 DB만 사용해 다음을 확인해야 합니다.

1. 각 브라우저의 실제 ID 등록 후 버튼과 배정된 단축키로 저장하고 앱에서 URL·제목 확인.
2. 같은 브라우저의 여러 창·탭에서 현재 탭만 저장되는지 확인.
3. 앱 미실행, 호스트 미등록, 잘못된 ID, DB 저장 실패에서 성공 표시가 없는지 확인.
4. `chrome://`, `edge://`, `file://` 거절과 합성 query·fragment 원문 보존 확인.
5. 확장 등록 해제 후 저장 실패 안내, 앱 책갈피 유지 확인.

## 공식 근거

- [Chrome activeTab: 사용자 동작과 현재 탭 metadata](https://developer.chrome.com/docs/extensions/develop/concepts/activeTab)
- [Chrome native messaging: manifest, HKCU 등록, protocol, 호출 origin](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)
- [Microsoft Edge native messaging: 별도 등록 경로와 실제 확장 ID](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging)
- [Chrome commands: 사용자 설정과 단축키 충돌](https://developer.chrome.com/docs/extensions/reference/api/commands)
- [Edge 단축키: Ctrl+Shift+Y는 컬렉션](https://support.microsoft.com/en-us/edge/keyboard-shortcuts-in-microsoft-edge)
