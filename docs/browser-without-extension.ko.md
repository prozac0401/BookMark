# 확장 프로그램 없이 웹페이지 갈무리

일반 Edge/Chrome 창에서 웹페이지 본문에 포커스를 둔 뒤 BookMark의 갈무리 단축키를 누릅니다. Windows 접근성 API로 주소 표시줄과 현재 문서의 URL·제목을 확인하고 저장합니다. 별도 확장 프로그램이나 브라우저 설정 변경은 필요하지 않습니다. 저장한 웹 책갈피는 기본 브라우저로 열립니다.

주소 표시줄에 `https://`나 `www.`가 생략되어도 현재 문서가 제공하는 전체 URL을 저장합니다. 주소를 두 차례 확인하며 탭이나 페이지가 바뀌면 저장을 중단합니다. 주소 표시줄에서 입력 중인 주소는 아직 방문한 페이지로 간주하지 않습니다. HTTP/HTTPS URL만 지원합니다.

전체 화면·주소 표시줄 없는 앱 창·화면 분할·사이드바·관리자 권한 브라우저·접근성이 제한된 환경에서는 자동 확인이 어려울 수 있습니다. 이때 트레이의 **웹페이지 URL로 추가…**를 이용하면 확장 프로그램 없이도 주소와 제목을 직접 등록할 수 있습니다. 확장 프로그램을 사용하는 기존 방식도 계속 사용할 수 있습니다.

BookMark는 브라우저 키 입력, 클립보드, 로그인 쿠키 또는 비밀번호를 읽지 않습니다. 접근성 탐색은 브라우저의 UI와 최상위 문서 메타데이터에서 멈추며, 웹페이지 본문·입력 필드·하위 프레임을 탐색하지 않습니다. 인증된 URL을 저장하더라도 다시 열 때의 로그인과 접근 권한은 브라우저 및 서버가 처리합니다.

## 확인 항목

- 확장 프로그램을 제거하거나 비활성화한 Edge와 Chrome에서 일반 HTTPS 페이지를 저장하고 다시 열기
- 사내 HTTP 주소, 쿼리·앵커가 포함된 주소, 한글 경로, 긴 제목 저장
- 서로 다른 URL과 같은 제목을 가진 두 탭을 전환하면서 다른 페이지가 저장되지 않는지 확인
- 주소 표시줄에 다른 주소를 입력만 한 상태에서는 자동 저장하지 않는지 확인
- 새 탭, 브라우저 설정, 로컬 파일 등 지원하지 않는 주소가 저장되지 않는지 확인
- 자동 갈무리가 불가능한 창에서 **웹페이지 URL로 추가…**로 저장 후 재실행하기

자동 검사는 브라우저 메타데이터의 URL 비교·변경 감지·입력 중 거부·기한 초과·제목 제한을 주입 경계에서 검증합니다. 실제 Edge/Chrome 버전과 회사 정책에 따른 접근성 노출은 별도 실기 확인 대상입니다.

구현 근거: [Microsoft UI Automation 검색](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.findfirst), [Chromium 문서 URL의 Value 제공](https://chromium.googlesource.com/chromium/src/+/main/ui/accessibility/platform/ax_platform_node_win.cc), [문서의 Value 패턴 지원](https://chromium.googlesource.com/chromium/src/+/main/ui/accessibility/platform/ax_platform_node_delegate_utils_win.cc).
