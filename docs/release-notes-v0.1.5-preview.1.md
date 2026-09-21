# WorkBookmark 0.1.5 Preview 1

웹 URL의 Office 문서가 사이트에 먼저 접속해야 열리거나, 이미 열렸는데 확인 실패로 안내되던 흐름을 개선했습니다. 확장 프로그램 없는 웹 책갈피 저장과 사용자별 MSI 설치를 추가했습니다.

## 변경 사항

- Office 문서가 최초 요청 후 4초간 나타나지 않으면 같은 사이트 루트에 내부 접속합니다. 접속을 마친 뒤에도 문서가 없음을 확인한 경우 한 번만 다시 엽니다.
- 웹 Office 준비를 최대 45초 기다립니다. 일시적인 시작·통신 지연을 허용하고, 위치 이동 직후 선택 정보도 최대 2초간 확인합니다.
- 로그인 중 클릭·입력은 문서 관찰을 끊지 않습니다. 사용자 입력 뒤에는 위치를 강제로 바꾸거나 다시 열지 않습니다. 문서 열림과 위치 복원 성공을 구분해 안내합니다.
- Edge·Chrome에서 Ctrl+Alt+B로 URL·제목을 읽습니다. 확장 프로그램은 필요하지 않습니다. 주소를 읽을 수 없는 환경은 트레이의 ‘웹페이지 URL로 추가…’를 사용할 수 있습니다.
- MSI는 현재 사용자에게 설치하고 Windows 로그인 시 자동 실행합니다. 시작 메뉴와 제어판 제거를 지원하며, 책갈피·메모·설정은 제거 후에도 보존합니다. 자동 실행은 설정에서 끌 수 있습니다.

## 설치

`WorkBookmark-0.1.5-win-x64.msi`를 실행하세요. 기존 ZIP판이 실행 중이면 먼저 트레이에서 종료합니다. .NET 별도 설치나 관리자 실행은 필요하지 않습니다. 설치 후 Ctrl+Alt+B로 저장하고 Ctrl+Alt+J로 목록을 엽니다.

포터블 사용에는 `WorkBookmark-0.1.5-win-x64.zip`을 제공합니다. `Source.zip`, `SHA256SUMS.txt`, MSI 검증 JSON을 함께 제공합니다.

## 검증과 제한

Release 전체 빌드 경고 0·오류 0, 자동검사 **396항목 통과**. Office URL 경계 55개, 확장 없는 브라우저 주소 판정 30개, IPC 16개, Desktop/직접 URL 입력/설치 종료 62개 등을 포함합니다. MSI 구조·관리 이미지 추출·전체 파일 해시 결과는 첨부한 `.validation.json`에 기록합니다.

실제 사내 AD 환경, 회사 정책이 적용된 Edge·Chrome, MSI 설치·제거·재부팅 수용시험은 별도입니다. 자동검사 통과를 이들 실기검증 완료로 표시하지 않습니다.

내부 접속은 Windows Internet 인증·쿠키 처리를 이용합니다. Chrome/Edge 전용 쿠키, JavaScript 로그인, MFA가 필요한 사이트에서는 ‘사이트 로그인’으로 인증을 마친 뒤 다시 실행해야 할 수 있습니다. 보안 정책이나 접근 권한을 우회하지 않습니다.

브라우저의 전체 화면·분할 탭·앱 창·접근성 제한 환경에서는 URL 직접 입력을 이용하세요. MSI와 실행 파일은 코드 서명 인증서가 없어 서명되지 않았습니다.

[빠른 시작](https://github.com/prozac0401/BookMark/blob/main/docs/quick-start.ko.md) · [Office 인증 복구와 확인 흐름](https://github.com/prozac0401/BookMark/blob/main/docs/office-url-support.ko.md) · [확장 없는 브라우저 저장](https://github.com/prozac0401/BookMark/blob/main/docs/browser-without-extension.ko.md) · [MSI 설치·제거·검증](https://github.com/prozac0401/BookMark/blob/main/docs/windows-installer.ko.md)
