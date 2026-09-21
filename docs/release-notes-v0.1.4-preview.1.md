# WorkBookmark 0.1.4 시험판 1

회사 인증 후 **데스크톱 Excel·Word·PowerPoint에서 웹 URL로 연 문서의 작업 위치**를 갈무리하고 재개할 수 있도록 확장했습니다. 배포 태그는 `v0.1.4-preview.1`, 실행 파일 버전은 `0.1.4`, 브라우저 확장 버전은 `0.2.2`입니다.

## 변경 내용

- Office 문서가 제공하는 HTTP/HTTPS 원본 주소와 Excel 시트·셀, Word 본문 문자 위치, PowerPoint SlideID를 저장합니다.
- 열린 문서는 URL로 구분하고 기존 창에서 위치를 복원합니다. 닫힌 문서는 해당 Office 앱으로 열기를 요청한 뒤 위치 복원을 확인합니다.
- 웹 문서에 로컬 파일 존재 확인을 적용하던 제한을 제거했습니다. 인증은 Windows·Office가 담당하며, BookMark가 비밀번호나 쿠키를 수집하지 않습니다.
- 로그인·로딩·사용자 입력으로 위치 이동을 확인하지 못한 경우 성공으로 표시하지 않고 재개 방법을 안내합니다.
- 한글·공백 URL, 같은 파일명의 다른 주소, 작업 위치별 중복 구분과 기존 메모 유지에 대한 검사를 추가했습니다. DB 스키마 변경은 없습니다.

## 실행과 업데이트

1. 기존 WorkBookmark를 트레이 메뉴에서 종료합니다.
2. `WorkBookmark-0.1.4-win-x64.zip`을 **새 폴더에 전체 압축 해제**하고 `WorkBookmark.exe`를 실행합니다. .NET SDK 설치는 필요하지 않습니다.
3. 인증 후 데스크톱 Office 문서에서 **Ctrl+Alt+B**로 저장하고 **Ctrl+Alt+J → 항목 선택 → Enter**로 재개합니다.
4. 재인증 안내가 나오면 Office에서 로그인·문서 열기를 마친 뒤 같은 책갈피를 다시 실행합니다.

기존 사용자별 로컬 책갈피 DB를 사용합니다. 브라우저 확장의 native host 경로가 이전 설치 폴더를 가리키는 경우 [브라우저 설치 안내](https://github.com/prozac0401/BookMark/blob/v0.1.4-preview.1/browser-extension/README.md)에 따라 새 폴더의 호스트를 등록하세요.

첨부 파일은 실행 ZIP, 해당 태그 커밋에서 생성한 `Source.zip`, `SHA256SUMS.txt`, 배포 검증 기록 `VALIDATION.json`입니다.

## 검증과 제한

- Release 전체 빌드: **경고 0·오류 0**.
- 자동검사 **333항목 통과**. 이번에 추가한 Office URL 관련 검사는 정책/저장소 7, IPC 3, Windows 경계 29항목입니다. [전체 실행 로그](https://github.com/prozac0401/BookMark/blob/v0.1.4-preview.1/docs/evidence/office-url-2026-09-21/build-and-tests.log).
- **사내 AD 인증·실제 웹 Office 문서 실기검증은 미실행이며 사용자가 진행합니다.** 자동검사 통과를 실제 회사 환경 지원 완료로 표시하지 않습니다.
- 보호된 보기·DRM/IRM 제한을 해제하지 않습니다. Office가 원본 URL 대신 로컬 임시 파일 경로만 제공하거나, 주소가 변경·리다이렉트되는 경우 원본 웹 주소를 추측하지 않습니다.
- 브라우저 Office 웹앱의 내부 위치 저장, 원본 파일 내용 보관, 미저장 Office 편집 내용 복원은 이번 변경 범위에 포함되지 않습니다.
- URL의 쿼리와 fragment는 보존되므로 주소에 포함된 토큰도 로컬 DB에 저장될 수 있습니다.

세 앱 각각의 열린/닫힌 문서, 재인증, 한글·공백, 문서 구분, 권한 변경 검증 방법은 [사용 방법과 자체 실기검증 안내](https://github.com/prozac0401/BookMark/blob/v0.1.4-preview.1/docs/office-url-support.ko.md)를 참고하세요. 기존 환경별 미검증 항목은 [호환성표](https://github.com/prozac0401/BookMark/blob/v0.1.4-preview.1/docs/compatibility.md)에 유지합니다.
