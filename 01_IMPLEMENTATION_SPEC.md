# 업무 책갈피 — 구현 명세 v0.1

> 최신 변경(0.1.3): 메모장 새 문서·미저장 본문은 파일 선택 없이 자동 보관하고 복원합니다. 이전의 원본 선택/미저장 거절 설명보다 [새 보관 계약](docs/notepad-snapshot-and-test-plan.ko.md)이 우선합니다.

> 2026-09-19 요구 변경: 사용자의 추가 구현 및 Edge/Chrome 지원 요청에 따라 [확장 요구명세](docs/extension-requirements.md)를 적용합니다. 아래 초기 v1 제외/후속 문구보다 이 확장 명세가 우선하며, 원본 기준은 이력으로 보존합니다.

작성일: 2026-09-18  
제품 가칭: 업무 책갈피 / WorkBookmark  
문서 상태: 개발 착수용. 프로그램 구현 및 업무 PC 실기 검증은 아직 수행하지 않음.

## 1. 만들려는 것과 판단 기준

평소처럼 일하다가 단축키로 현재 작업 대상에 표시를 남기고, 나중에 작은 최근 목록에서 선택해 돌아오는 Windows 개인용 도구를 만든다.

**새 업무 시작 시 조작 0회 → 중단 시 단축키 1회 → 재개 시 단축키 + Enter.**

업무명·분류·관련 자료를 먼저 등록하지 않는다. 메모가 없어도 경로 또는 셀 위치로 돌아갈 수 있어야 한다. 메모는 머릿속 진행 상황을 보충하는 선택 기능이다.

이 문서의 MUST는 v1 필수 요구사항이다. ‘검증 대상’은 구현 후보이며 실기 시험 통과 전까지 지원 완료로 표시하지 않는다. 성능 수치는 측정 전 개발 목표다.

### 결정의 출처

| 구분 | 내용 |
|---|---|
| 대화에서 합의한 요구 | 탐색기 파일·폴더, Excel 활성 셀, 작은 조작, 선택 메모, 원본 저장 명령 금지, 웹 별도 모듈 |
| 이 명세에서 정한 구현 기본값 | C#/.NET 10/WinForms, 사용자별 SQLite, 기본 단축키, 중복·정렬 정책, 작업 프로세스 격리 |
| 선행 검증 필요 | 탐색기 활성 탭 판별, Excel 다중 창/프로세스 연결, 업무 PC DRM, Office 비트수 조합 |

구현 기본값은 Codex가 착수할 수 있도록 정한 설계 결정이다. 사용자가 별도로 기술 스택까지 지정했다는 의미는 아니다. 기존 저장소에 명확한 기술 기준이 있다면 호환성을 확인해 결정 기록에 남긴다.

## 2. v1 범위와 지원 계약

| 대상 | 저장 정보 | 재개 동작 | v1 상태 |
|---|---|---|---|
| 탐색기 현재 실제 폴더 | 절대 폴더 경로 | 탐색기로 폴더 열기 요청 | 필수 |
| 탐색기 단일 선택 폴더 | 선택 폴더의 절대 경로 | 탐색기로 폴더 열기 요청 | 필수 |
| 탐색기 단일 선택 파일 | 파일 절대 경로 | 문서 열기 또는 폴더에서 해당 항목 표시 | 필수 |
| 설치형 Excel의 저장된 통합문서 | 파일 경로 + 시트명 + 활성 셀의 절대 A1 주소 | 동일 통합문서를 찾아 시트·셀로 이동 | 필수 |
| 접근 가능한 UNC/네트워크 드라이브 | 위와 동일 | 연결 가능한 경우 재개 | 별도 호환성 시험 후 지원 표시 |
| Chrome/Edge 웹페이지 | 제목 + URL | 기본 브라우저로 URL 열기 | v1 본체에 구현하지 않음. 후속 선택 모듈 |
| Word/PPT/PDF 프로그램 내부 위치 | 해당 없음 | 해당 없음 | 후속 |
| VS Code/Cursor 내부 코드 위치 | 해당 없음 | 해당 없음 | 후속 |
| Outlook 메일, Codex 대화, 터미널·디버거 세션 | 해당 없음 | 해당 없음 | 제외 |

파일에 붙인 책갈피와 앱 내부 위치에 붙인 책갈피는 다르다. `docx` 파일을 탐색기에서 선택해 저장할 수 있지만, Word에서 단축키를 눌러 현재 문단을 저장하는 기능은 없다.

### 환경 기준

- Windows 11 x64의 지원 중인 빌드, 일반 사용자 권한, 대화형 로그인 세션을 1차 대상으로 한다. 정확한 OS/Office 빌드는 시험 기록에 남긴다.
- 설치형 Microsoft 365 Excel 64비트를 우선 필수 시험 환경으로 한다. Excel 32비트는 별도 호환성 시험 대상으로 두며, x64 앱과의 연동을 검증하기 전 지원을 주장하지 않는다.
- Excel 미설치 PC에서도 탐색기 기능과 목록·메모는 동작해야 한다.
- Windows 10, ARM64, 웹 Excel, 가상 데스크톱 간 이동은 기본 지원 범위에서 제외한다. 개별 시험 결과 없이 확대하지 않는다.
- 관리자 권한으로 실행된 대상 앱, 보호된 보기, DRM 차단 상태는 지원 불가 또는 사용자 조치 필요로 처리한다. 권한 상승·정책 우회 기능은 없다.

## 3. 사용자 동작과 화면

### 3.1 실행과 첫 사용

압축 해제 후 `WorkBookmark.exe`를 실행한다. 트레이에 상주하고 일반 관리 창은 열지 않는다. 최초 실행에만 단축키와 지원 범위를 짧게 안내한다. 업무 등록·로그인·계정 생성은 없다.

시작프로그램 등록은 기본 꺼짐이다. 설정에서 사용자가 켜면 사용자 시작프로그램 폴더에 바로가기를 만든다. 관리자 권한이나 시스템 범위 등록이 필요하지 않다. 두 번째 앱 실행은 기존 프로세스의 최근 목록을 열고 종료한다.

| 동작 | 기본 단축키/접점 | 결과 |
|---|---|---|
| 현재 위치 남기기 | `Ctrl+Alt+B` | 현재 대상 확인 후 저장, 비활성 알림 |
| 최근 책갈피 열기 | `Ctrl+Alt+J` | 작은 목록, 맨 위 항목 선택 |
| 이어가기 | 목록의 `Enter` 또는 항목 한 번 클릭 | 즉시 재개 요청 |
| 목록 닫기 | `Esc` 또는 외부 클릭 | 대상 열기 없이 닫음 |
| 메모 | 저장 알림의 ‘메모’ 또는 목록의 보조 메뉴 | 해당 책갈피의 한 줄 편집 |
| 설정/종료 | 트레이 메뉴 | 단축키·시작프로그램·데이터 위치·진단 / 종료 |

단축키는 개발 기본값이며 충돌 없는 조합이라고 보장하지 않는다. `RegisterHotKey`와 `MOD_NOREPEAT`를 사용한다. 등록 실패 시 해당 단축키만 비활성으로 명확히 표시하고 설정에서 바꾸게 한다. 기존 단축키가 아직 유효하다면 새 조합 등록 성공 전까지 유지한다. 예약키를 사용하거나 충돌을 무시하지 않는다. [S1]

### 3.2 중단할 때

1. `WM_HOTKEY` 처리 시 알림이나 앱 창을 띄우기 **전에** foreground HWND/PID 등 일시 정보를 얻는다.
2. 지원 어댑터가 대상과 위치를 읽는다. 이때 제목 입력·확인 창은 없다.
3. 로컬 DB 트랜잭션이 커밋된 뒤에만 ‘책갈피를 남겼습니다’라고 표시한다.
4. 성공 알림은 약 3초 표시하고 포커스를 가져오지 않는다. 자동 제목, 시각, Excel이면 시트·셀을 표시한다.
5. 저장 실패는 짧은 이유를 알린다. 실패 기록을 정상 책갈피처럼 만들지 않는다.

예: `책갈피를 남겼습니다 · 확정명단.xlsx · 확정자!D127  [메모]`

트레이 메뉴를 클릭한 순간에는 foreground가 바뀌므로, v1 트레이 메뉴에 ‘현재 위치 저장’을 넣지 않는다. 정확한 원래 대상을 보존할 다른 경로가 검증되기 전까지 저장 접점은 전역 단축키 하나로 유지한다.

### 3.3 재개할 때

최근 목록은 최근에 **저장한 순서**(`capture_sequence` 내림차순)로 20개를 보여주고 저장 시각을 함께 표시한다. PC 시계가 바뀌어도 순서가 뒤집히지 않는다. 목록 안의 검색창으로 이름·경로·시트명·메모를 검색할 수 있고, 검색 결과는 모든 미삭제 기록에서 같은 순서로 최대 100개 표시한다. 더 있으면 검색어를 좁히도록 안내한다.

각 행의 기본 표시:

- 주 행: 자동 파일명 또는 폴더명 + 상대 시각.
- 보조 행: 경로. Excel이면 `시트명 · 셀 주소`를 앞에 붙임.
- 메모가 있으면 한 줄 표시. 저장 당시 미저장 변경이 있으면 작은 표식.
- 같은 이름의 파일은 서로 다른 경로가 눈에 보이게 표시. 도구 설명 또는 보조 메뉴로 전체 경로 복사 가능.

맨 위 항목에서 Enter를 누르면 상세 화면을 거치지 않는다. 검색창에 입력이 없어도 선택은 유지한다. 위/아래로 선택, Enter로 재개한다. 한국어 IME 조합을 확정하는 Enter가 재개까지 실행되지 않도록 처리한다.

메모는 재개 전에 목록에서 읽을 수 있다. 목록을 열거나 재개하는 것만으로 저장 시각을 갱신하지 않는다. 재개 기록은 별도 필드로 남긴다. 목록을 보고 있는 중에 정렬이 바뀌어 선택 대상이 달라지면 안 된다.

### 3.4 메모·삭제·경로 수정

- 메모는 최대 500자, 한 줄이다. Enter 또는 편집창 외부 클릭으로 저장하고 Esc는 현재 편집만 취소한다. IME 조합 중 Enter는 확정만 수행한다.
- 메모 입력 전 책갈피 저장은 이미 끝나 있어야 한다. 메모 저장 실패나 Esc가 책갈피를 취소하지 않는다. 실패 시 편집 내용을 보존하고 재시도할 수 있게 한다.
- 같은 대상에 다시 꽂으면 기존 메모를 유지한다. 알림에는 ‘이전 메모 유지’ 표시를 하고, 메모를 새로 작성한 것처럼 시각을 갱신하지 않는다.
- 목록 보조 메뉴의 ‘목록에서 지우기’는 책갈피만 숨긴다. 원본은 삭제하지 않는다. 10초간 ‘되돌리기’를 제공한다. DB에서는 `deleted_at_utc`를 사용하며 자동 만료·일괄 삭제는 v1에 없다.
- 경로를 찾지 못한 항목의 보조 메뉴에서만 ‘위치 다시 지정’을 제공한다. File/Folder 선택기로 같은 대상 종류를 고르게 한다. 새 경로·기존 메모·Excel 위치 유지 여부를 함께 보여주고 사용자가 적용한다.
- 경로 수정 시 기존 시트·셀의 존재를 재검증한다. 이 기능은 경로만 변경한다. 기존 시트·셀이 없으면 기존 기록을 유지하고, 원하는 위치에서 새 책갈피를 남기도록 안내한다. 다른 파일을 자동 검색해 대체하거나, `A1` 등 임의 위치로 성공 처리하지 않는다. 기존 항목과 중복 충돌하면 변경을 취소하고 해당 기존 항목이 있음을 알린다.

## 4. 탐색기 어댑터

### 4.1 저장 규칙

현재 화면이 실제 파일 시스템 폴더이며 활성 탐색기 뷰를 특정한 경우에만 다음 규칙을 적용한다.

| 선택 개수 | 저장 대상 |
|---|---|
| 0개 | 현재 폴더 |
| 1개, 파일 | 그 파일 |
| 1개, 폴더 | 그 폴더 |
| 2개 이상 | 저장하지 않음. ‘파일이나 폴더를 하나만 선택해 주세요.’ |

폴더·선택 항목의 공식 Shell 객체에서 경로를 얻는다. `IShellWindows`/Shell view 계열을 통해 foreground 창의 활성 뷰와 연결하는 어댑터를 설계한다. `Folder`/`SelectedItems`는 폴더와 선택 정보의 근거지만, 그것만으로 Windows 11의 활성 탭 매핑이 보장되지는 않는다. [S3]

**선행 PoC 필수:** 다중 창·다중 탭에서 현재 보이는 뷰를 구분하고, 같은 top-level HWND를 공유하는 후보 중 첫 번째 것을 임의 선택하지 않는다. 공식 인터페이스 중심으로 검증한다. 특정 비공개 창 클래스나 UI 구조에 의존해야 한다면 해당 빌드 의존성을 별도 격리·기록한다. Windows 업데이트로 불확실해지면 저장을 거절한다.

‘홈’, ‘최근’, ‘이 PC’, 라이브러리, 검색 결과, 휴지통, ZIP 내부, 휴대폰 MTP 등의 가상 뷰는 v1에서 **화면 전체를 제외**한다. 그 화면에서 실제 파일이 선택되었더라도 예외로 받지 않아 범위를 단순하게 유지한다. 바탕화면 자체와 파일 열기/저장 대화상자도 탐색기 지원에 포함하지 않는다.

### 4.2 재개 규칙

폴더는 Windows Shell에 열기를 요청한다. 파일은 아래 정책으로 처리한다.

| 유형 | 기본 동작 |
|---|---|
| 문서 허용 목록의 확장자 | 연결된 프로그램에 `open` 요청 |
| 실행/스크립트/바로가기/기타 형식 | `SHOpenFolderAndSelectItems`로 위치 표시 요청 |

초기 문서 허용 목록: `.doc`, `.docx`, `.docm`, `.rtf`, `.xls`, `.xlsx`, `.xlsm`, `.xlsb`, `.csv`, `.ppt`, `.pptx`, `.pptm`, `.pdf`, `.txt`, `.md`, `.hwp`, `.hwpx`, `.png`, `.jpg`, `.jpeg`, `.gif`, `.bmp`, `.tif`, `.tiff`.

`.exe`, `.com`, `.msi`, `.bat`, `.cmd`, `.ps1`, `.vbs`, `.js`, `.py`, `.lnk`, `.url`, `.hta`, `.scr` 등은 실행하지 않는다. 허용 목록 밖 확장자는 모두 위치 표시다. 이 목록은 ‘파일이 안전하다’는 판정이 아니며, Office·연결 프로그램의 기존 보안 정책은 그대로 적용된다.

경로는 셸 명령 문자열에 이어 붙이지 않는다. `cmd /c`, PowerShell, 클립보드, 키 입력 흉내를 파일 열기 수단으로 사용하지 않는다. Shell API에 경로와 동작을 별도 인수로 전달한다. 일반 경로/UNC만 허용하고 `javascript:` 등의 URI, 장치 경로, 대체 데이터 스트림은 받지 않는다. [S4][S5]

일반 파일은 이미 열려 있는 문서의 정확한 창을 식별할 수 없다. **재개는 연결 프로그램에 열기 요청까지** 보장하며, 창 재사용·중복 창 생성 여부는 해당 앱이 결정한다. Excel 좌표 책갈피만 아래의 별도 연결·검증 절차를 사용한다.

## 5. Excel 어댑터

### 5.1 저장 전제와 획득 정보

지원 형식: `.xlsx`, `.xlsm`, `.xlsb`, `.xls`의 일반 통합문서. `.csv` 등은 탐색기 파일 책갈피로 사용할 수 있으나 Excel 좌표 책갈피에는 포함하지 않는다.

한 번 이상 저장되어 실제 로컬/UNC 경로를 가진 통합문서의 일반 워크시트가 대상이다. HTTPS 주소로만 식별되는 클라우드 문서, 미저장 새 통합문서, 보호된 보기, 차트 시트, 활성 셀을 읽을 수 없는 상태는 저장하지 않는다. OneDrive 등의 동기화 파일은 실제 로컬 경로를 얻고 해당 환경 시험을 통과한 경우에만 로컬 파일로 취급한다.

저장 값:

| 필드 | 얻는 정보 |
|---|---|
| `path` | 해당 Workbook의 실제 전체 파일 경로 |
| `sheet_name` | 활성 셀이 속한 Worksheet의 이름 |
| `cell_address` | 단일 활성 셀의 절대 A1 주소. 예: `$D$127` |
| `had_unsaved_changes` | 캡처 시점 `Workbook.Saved == false` 여부 |
| `display_name` | 파일명. 창 제목에서 경로를 역추정하지 않음 |

미저장 새 문서는 `Workbook.Path`가 비어 있는지와 실제 전체 경로의 유효성을 함께 확인해 판정한다. `Saved=false`만으로 새 문서라고 판정하지 않는다. 미저장 변경 표식은 **저장 당시 상태**이며 실시간 상태가 아니다. `Workbook.Saved`는 읽기만 한다. `Saved=true`를 대입하면 저장 경고 의미를 바꿀 수 있으므로 금지한다. [S7][S8]

여러 셀/영역 선택 시 활성 셀 하나만 기록한다. 병합 셀은 읽은 활성 셀과 재개 시 검증 가능한 기준 주소를 일관되게 사용하고 선행 시험에서 규칙을 확정한다. 셀 값·수식·행의 사람 이름을 읽거나 보관하지 않는다. [S7]

### 5.2 정확한 인스턴스 연결

전역 `GetActiveObject("Excel.Application")` 결과 하나의 `ActiveWorkbook`을 그대로 쓰는 구현은 금지한다.

검증 후보 경로는 foreground Excel 창 → 그 창의 `EXCEL7` 하위 창 → `AccessibleObjectFromWindow(OBJID_NATIVEOM, IID_IDispatch)` → Excel `Window` 객체다. 이 API는 해당 Office 창의 네이티브 객체 모델 접근 경로를 제공한다. 실제 Excel 빌드·Office 비트수별 연결과 다중 창 동작은 PoC로 확인한다. [S6]

연결된 Window/Workbook/Worksheet/ActiveCell의 부모 관계를 검증하고, 캡처 시도 앞뒤에 동일 대상인지 확인한다. 동일 Application의 다른 창으로 바뀌는 경우도 검사한다. 여러 후보면 `AmbiguousTarget`, 읽는 도중 대상이 바뀌면 `ContextChanged`로 실패한다. 정확한 순간의 원자적 스냅샷 API는 아니므로 조용히 다른 탭/문서를 대신 저장하지 않는다.

셀 편집 중, 이름 바꾸기, 모달 대화상자, Excel 응답 지연 시 짧게 실패할 수 있다. Esc·Enter를 보내 편집을 끝내거나 대화상자를 닫지 않는다.

### 5.3 재개 절차

1. 현재 열려 있는 Excel 창들을 열거해 전체 경로가 일치하는 통합문서를 찾는다. 표시 이름만으로 비교하지 않는다. 별도 인스턴스도 후보에 포함한다. 열거 중 일부 인스턴스에 연결하지 못했다면 ‘일치 문서 없음’으로 단정해 새로 열지 않는다. 결과를 확정하지 못했다고 알린다.
2. 같은 경로가 여러 인스턴스에서 열려 있고 정확히 하나를 고를 수 없으면 모호함을 알린다. 미저장 상태가 다른 두 복사본 중 하나를 임의 선택하지 않는다. 예외 상황에서만 후보 창 선택을 제공할 수 있다. 동일 인스턴스의 동일 Workbook에 속한 여러 Window는 복사본 충돌과 구분하며, 현재 선택된 해당 창을 우선하고 없으면 검증된 보이는 창 하나를 사용한다.
3. 일치 문서가 없고 해당 경로를 재개 대상으로 확인할 수 있을 때 Windows Shell을 통해 해당 파일 열기를 **한 번** 요청한다. `.xlsx` 등이 Excel 외 앱에 연결되어 있으면 파일 열기와 셀 이동을 구분해 안내한다.
4. 제한 시간 동안 그 경로의 Excel 문서가 나타나는지 확인한다. 파일명만 같은 다른 통합문서에는 연결하지 않는다.
5. 시트가 존재하고 보이는 상태이며 셀 주소가 유효한지 검사한다. 숨겨진 시트·숨겨진 행/열·필터로 감춰진 셀은 속성을 바꾸지 않고 위치 이동 실패로 처리한다. 시트 보호로 선택이 제한되는 경우도 실패다.
6. 대상 Window/Worksheet를 활성화하고, 검증된 Range 객체를 이용해 셀로 이동한다. 문자열을 Excel 수식/매크로 실행에 전달하지 않는다. `Application.Goto`는 구현 후보이며 활성 셀과 부모 경로를 다시 읽어 이동 결과를 확인한다. [S9]
7. HWND가 유효한지 확인한 뒤 화면 전환을 요청한다. 셀 이동 결과와 foreground 성공 여부는 별도로 기록한다. Windows가 전면 전환을 거부하면 ‘셀로 이동했습니다. Excel 창을 선택해 주세요.’라고 알리고 필요 시 작업표시줄 표시를 사용한다. [S2]

통합문서를 프로그램 방식으로 여는 `Workbooks.Open`은 v1 재개 기본 경로로 사용하지 않는다. 이 API의 기본 매크로 동작은 일반적인 사용자 열기와 다르게 취급할 여지가 있어 별도 보안 설계가 필요하다. `AutomationSecurity`, `DisplayAlerts`, `EnableEvents`, 계산 옵션 등의 사용자 Excel 전역 설정을 변경하지 않는다. [S10]

### 5.4 ‘원본을 건드리지 않음’의 정확한 뜻

도구는 `Save`/`SaveAs`/`Close`/`Quit`, 셀 값·수식 쓰기, `Saved=true`, 매크로 실행, 보호 해제, 필터·정렬 변경을 호출하지 않는다. 캡처는 메타데이터 조회만 수행한다.

다만 **파일 열기와 시트·셀 이동 자체는 Excel의 기존 이벤트, 추가 기능, 자동저장 동작을 유발할 수 있다.** 따라서 모든 환경에서 원본 파일 바이트나 통합문서 상태가 절대 바뀌지 않는다고 보장하지 않는다. 시험에서는 도구가 금지 명령을 호출하지 않는지와 기본 fixture에서 예상 밖 내용 변경이 없는지를 구분해 확인한다. [S11]

저장 좌표는 같은 데이터를 의미하지 않는다. 행 삽입·정렬·내용 변경 후에도 기록한 좌표로 이동하며, 사람·레코드를 추적하지 않는다. 메모가 ‘완료’를 증명하지도 않는다.

## 6. 비동기 작업, 지연, 실패

### 6.1 프로세스 구조

| 구성 | 책임 |
|---|---|
| `WorkBookmark.App` | 트레이, 전역 단축키, 목록, 메모, DB, 요청 수명 관리 |
| `WorkBookmark.Core` | 책갈피 모델, 중복·정렬, 상태·오류, 어댑터 계약 |
| `WorkBookmark.Windows` | Win32/Shell/Excel 어댑터, 경로 정책 |
| 동일 EXE의 `--worker` 모드 | 요청별 격리된 STA 프로세스, 메시지 루프, 외부 COM/파일 접근 |
| 테스트 프로젝트 | Core 단위시험, worker 장애시험, Windows/Excel 통합시험 |

기본 스택: C# + .NET 10 LTS + WinForms(`net10.0-windows`) + `Microsoft.Data.Sqlite`. Windows 전역 단축키·트레이·COM 연동에 집중한다. SDK 및 패키지 버전은 실제 빌드 가능한 안정 버전으로 고정하고 lock 파일을 커밋한다. 런타임 지원 근거는 [S12].

UI 스레드에서 COM, UNC 존재 확인, Shell 실행을 수행하지 않는다. `Task.Run` 안의 COM에 타임아웃만 씌우고 호출이 취소됐다고 가정하는 구현은 금지한다. Office 스레딩 제약은 [S16]을 참고한다. 같은 EXE를 쓰더라도 `--worker` 분기는 일반 앱의 단일 실행 mutex 검사보다 먼저 처리해 두 번째 UI 실행으로 오인하지 않게 한다.

초기 구현은 **동시에 외부 작업 1개, 요청마다 worker 1개**로 제한한다. worker는 STA와 메시지 펌프를 갖고 해당 요청의 COM 참조를 내부에서만 사용한다. App에 COM 객체나 RCW를 전달하지 않고 직렬화 가능한 DTO만 돌려준다. 평소에는 worker가 남아 있지 않아야 한다. 성능 측정으로 필요가 확인될 때만 따뜻한 worker 재사용을 검토한다.

IPC는 부모가 만든 표준 입출력 파이프 또는 현재 사용자 전용 named pipe 중 하나를 택해 PoC에서 고정한다. 프로토콜 버전, request ID, 제한시간, 작업 종류, 일시 대상 정보, 결과 코드만 전달한다. 프레임 크기는 최대 256 KiB로 제한하고 잘못된 메시지는 거절한다. 경로·메모를 커맨드라인 인수로 전달하지 않는다. 외부 네트워크 포트는 열지 않는다.

### 6.2 제한시간과 취소 의미

| 항목 | 개발 기본값 |
|---|---|
| 단축키 접수 피드백 | 200ms 이내 목표. 아직 저장 성공 문구를 표시하지 않음 |
| 캡처 worker 전체 기한 | 로컬 3초, 경로가 UNC/네트워크로 판명되면 최대 5초 |
| 재개/열기 관찰 전체 기한 | 15초 |
| 재개 중 안내 | 1초 초과 시 ‘여는 중’. UI의 목록/취소 조작은 계속 응답 |
| 동시에 진행할 외부 요청 | 1개. 누적 큐를 만들지 않음 |

예산은 개별 COM 호출마다 새로 시작하지 않고 요청 전체에 적용한다. 일시적 COM Busy의 읽기 호출은 남은 기한 안에서만 제한 재시도한다. 모든 재시도 전에 동일 대상인지 확인한다. 열기·셀 이동 같은 외부 동작은 응답이 불명확하면 자동 반복하지 않는다.

worker가 기한을 넘으면 해당 요청을 종료 상태로 고정하고 필요 시 **자체 worker만** 종료한다. Excel·Explorer 및 사용자 앱을 죽이지 않는다. `Kill(entireProcessTree: true)`나 연결 앱까지 상속되는 kill-on-close Job으로 정리하지 않는다. Shell로 열린 앱이 자식 관계에 놓이더라도 종료 대상에 포함하지 않는다. 늦게 도착한 결과는 request ID/종료 상태로 버리고 DB·알림·최근 순서에 반영하지 않는다.

**worker 종료는 이미 Excel/Shell에 전달된 동작을 되돌리거나 취소했다는 뜻이 아니다.** 이미 전달된 열기/셀 이동이 나중에 완료될 수 있다. 이 경우 ‘처리 결과를 확인하지 못했습니다. 대상 앱을 확인해 주세요.’라고 표시한다. 자동으로 다른 위치를 이동시키거나 원래 상태로 되돌리지 않는다.

재개를 시작한 Enter/클릭의 입력 처리가 끝난 시점을 기준점으로 삼고, 키 해제는 별도 취소로 보지 않는다. 목록이 닫히거나 Shell이 대상 Excel을 띄우는 정상 전환은 허용한다. 예상 전환 대상은 재개 직전 원래 창과 해당 요청에서 검증한 대상 창으로 제한한다. 사용자 입력과 연계해 관련 없는 앱으로 이동한 것이 확인되거나 새 편집을 시작한 것이 감지되면, 이후 새로운 셀 이동·포커스 전환 명령을 보내기 전에 중단한다. 입력 내용은 수집하지 않는다. 창 식별이 불명확한 경우도 후속 이동을 멈춘다. 감지와 외부 호출이 원자적이지 않으므로 이미 보낸 명령까지 막는다고 약속하지 않는다. 늦게 열리는 문서를 강제로 닫지 않는다.

캡처 중 추가 캡처는 한 번의 ‘처리 중’ 안내 후 무시한다. 최근 목록은 열 수 있다. 재개 중 새 재개 요청은 기존 관찰을 중단한 후 새 요청을 시작할 수 있으나, 먼저 전달한 외부 동작의 결과 미확인 상태를 유지한다. 동일 항목의 연속 Enter/클릭은 중복 실행을 억제한다.

### 6.3 내부 결과와 표시 문구

| 내부 결과 | 사용자 표시 예 | 의미 |
|---|---|---|
| `CaptureCommitted` | 책갈피를 남겼습니다 | DB 반영 완료 |
| `UnsupportedTarget` | 이 화면의 작업 위치는 아직 지원하지 않습니다 | 대상 제외 |
| `AmbiguousTarget` / `ContextChanged` | 현재 작업 위치를 정확히 확인하지 못했습니다. 다시 눌러 주세요 | 다른 대상 추정 금지 |
| `MultipleSelection` | 파일이나 폴더를 하나만 선택해 주세요 | 캡처 거절 |
| `UnsavedWorkbook` | 파일로 저장한 뒤 책갈피를 남겨 주세요 | 새 문서 경로 없음 |
| `AppBusy` | 편집이나 대화상자를 마친 뒤 다시 눌러 주세요 | 자동 키 입력 금지 |
| `CaptureTimedOut` | 위치를 확인하지 못해 책갈피를 남기지 않았습니다 | 캡처 결과 폐기 |
| `PersistenceFailed` | 책갈피를 저장하지 못했습니다 | 메모리 성공을 저장 성공으로 취급하지 않음 |
| `OpenRequested` | 열기를 요청했습니다 | Shell 요청 접수만 확인 |
| `RevealRequested` | 파일 위치 표시를 요청했습니다 | 실행 대신 위치 표시 |
| `PositionRestored` | 기록한 셀로 이동했습니다 | 경로·시트·주소 재검증 완료 |
| `PositionRestoredFocusPending` | 셀로 이동했습니다. Excel 창을 선택해 주세요 | 위치 성공, 전면 이동 실패 |
| `OpenedPositionFailed` | 파일은 열렸지만 기록한 셀로 이동하지 못했습니다 | 문서 연결은 검증, 세부 위치 실패 |
| `ResumeOutcomeUnknown` | 처리 결과를 확인하지 못했습니다. 대상 앱을 확인해 주세요 | 외부 요청 후 시간초과/관찰 중단 |
| `TargetUnavailable` | 파일 또는 폴더에 접근할 수 없습니다 | 이동·삭제·권한·네트워크 등 |

파일을 열었다는 증거가 없으면 `OpenedPositionFailed`를 쓰지 않는다. Shell 반환값이나 생성 PID만으로 문서/위치 성공을 선언하지 않는다. [S4]

## 7. 데이터와 중복 정책

### 7.1 보관 위치와 필드

사용자별 `%LOCALAPPDATA%\WorkBookmark\bookmarks.db`에 SQLite로 보관한다. DB는 로컬 디스크에만 둔다. 네트워크 폴더·실행파일 위치·동기화 폴더를 기본 DB로 쓰지 않는다. 계정·서버·클라우드 동기화는 없다.

| 필드 | 타입/규칙 |
|---|---|
| `id` | UUID, 논리 식별자 |
| `kind` | `folder`, `file`, `excel_cell` |
| `path` | 유효성 검증된 절대 경로. 표시용 원문 보존 |
| `normalized_path` | 아래의 보수적 정규화 결과 |
| `sheet_name` | Excel 전용, 그 외 null |
| `cell_address` | Excel 전용, 절대 A1 단일 셀, 그 외 null |
| `display_name` | 실제 경로에서 얻은 자동 이름 |
| `note` | 기본 빈 문자열, 최대 500자 |
| `created_at_utc` | 최초 기록 시각 |
| `captured_at_utc` | 마지막으로 꽂은 시각 |
| `capture_sequence` | 앱 단일 writer에서 증가. 시계 변경 때도 순서 보장 |
| `note_updated_at_utc` | 마지막 명시적 메모 수정. 자동 캡처로 변경하지 않음 |
| `had_unsaved_changes` | Excel 캡처 시 true/false, 다른 종류 null |
| `last_resume_at_utc` | 마지막 재개 시도, nullable |
| `last_resume_result` | 마지막 재개 결과, nullable |
| `deleted_at_utc` | 삭제 표식, nullable |

DB 스키마 버전은 `PRAGMA user_version`으로 관리한다. 사용자 설정에는 설정 버전, 단축키 두 개, 시작프로그램 여부를 둔다. 설정 파일 교체도 원자적으로 수행한다.

HWND/PID/프로세스 생성시각/작업 요청 ID는 캡처·재개 중 메모리에만 둔다. 재시작 후 창 번호를 문서 정체성으로 사용하지 않는다.

### 7.2 중복의 단위

| 종류 | 중복 판정 키 |
|---|---|
| 폴더 | `folder + normalized_path` |
| 파일 | `file + normalized_path` |
| Excel 위치 | `excel_cell + normalized_path + sheet_name + cell_address` |

같은 키를 다시 저장하면 기존 ID를 유지하고 캡처 시각·sequence·미저장 상태를 갱신한다. **메모는 보존한다.** 다른 셀은 별도 책갈피다. 일반 파일 책갈피와 같은 파일의 Excel 위치 책갈피도 별개다. 삭제 표식이 있던 같은 키를 다시 꽂으면 기존 기록과 메모를 복원하며 그 사실을 알린다.

정규화는 절대 경로화, 구분자 정리, 루트 외 후행 구분자 정리까지 한다. 임의 소문자화·Unicode 정규화·네트워크 접근을 통한 별칭 통합은 하지 않는다. v1 키 비교는 정규화된 문자열의 ordinal 비교로 보수적으로 처리한다. 대소문자/심볼릭 링크/UNC 별칭/매핑 드라이브 차이로 같은 파일의 책갈피가 둘 생기는 것은 허용한다. 서로 다른 파일을 합치는 것보다 우선한다.

DB 중복 키와 재개 시 열린 문서 식별은 구분한다. 열린 Excel에서 정규화 경로가 정확히 일치하면 우선 후보로 삼는다. 대소문자만 다른 경로 또는 확인된 매핑 별칭 등 잠재 동일 후보가 있으면 ‘문서 없음’으로 단정해 새로 열지 않는다. 동일 파일임을 안전하게 확인하지 못하면 식별 미확인으로 중단한다. 이름만으로 추정해 이동하지 않는다. 파일 식별자 기반 영구 중복 병합은 후속 범위다.

시트명 변경·파일 이동·이름 변경은 자동 추적하지 않는다. 파일 경로 변경은 실패 항목에서 경로 재지정으로 복구한다. 시트명 변경·시트 삭제·다른 셀로 변경하려는 경우는 원하는 셀에서 새 책갈피를 남기고 이전 항목을 필요 시 지운다. v1에 시트/셀 편집 관리 화면은 만들지 않는다. 주기적으로 모든 경로의 존재를 스캔하지 않는다.

### 7.3 예시 레코드

아래는 사람이 이해하기 위한 예시이며 저장 포맷은 SQLite다.

```json
{
  "id": "56edfd87-7c30-4d94-97df-88b2d6a39bd7",
  "kind": "excel_cell",
  "path": "D:\\업무\\A과정\\확정명단.xlsx",
  "normalized_path": "D:\\업무\\A과정\\확정명단.xlsx",
  "sheet_name": "확정자",
  "cell_address": "$D$127",
  "display_name": "확정명단.xlsx",
  "note": "중복 확인 완료. 취소자 반영부터.",
  "created_at_utc": "2026-09-18T05:32:00Z",
  "captured_at_utc": "2026-09-18T05:32:00Z",
  "capture_sequence": 1,
  "note_updated_at_utc": "2026-09-18T05:32:10Z",
  "had_unsaved_changes": true,
  "last_resume_at_utc": null,
  "last_resume_result": null,
  "deleted_at_utc": null
}
```

### 7.4 내구성과 최소 진단

- 저장/수정/메모/삭제 표식은 트랜잭션 단위. 쓰기 실패 시 기존 상태 유지.
- WAL 등 SQLite 설정은 실제 정합성과 배포 환경을 확인해 적용한다. 켜진 DB 파일 하나만 복사해 백업하지 않는다.
- 스키마 변경 전 SQLite의 일관된 백업 API를 사용한다. 실패하면 마이그레이션을 중단하고 기존 파일을 유지한다.
- 손상된 DB를 발견해도 자동 초기화하지 않는다. 원본을 보존하고 ‘기록을 읽을 수 없음’과 데이터 폴더 위치를 안내한다.
- 진단 로그는 코드·단계·시간·소요시간·앱/OS/Office 버전 정도만 기본 기록한다. 문서 제목·경로·메모·셀 값은 기본 로그에서 제외한다. 1 MiB씩 최대 3개로 순환한다.
- 데이터 DB에는 경로와 메모가 평문으로 남는다. 별도 암호화·보안 보관함이라고 표현하지 않는다. 텔레메트리·백그라운드 외부 통신은 없다.

## 8. 패키징과 운영

- 배포 기본값은 self-contained `win-x64` ZIP이다. 사용자 PC에 SDK/Python/Node 설치를 요구하지 않는다. .NET 단일 EXE 묶음은 크기·추출·COM/SQLite 배포 검증 후 선택하며 v1 필수 조건이 아니다. [S13]
- ZIP에는 실행에 필요한 런타임·의존 파일과 한국어 사용 안내를 포함한다. 전체 배포 폴더를 유지하도록 설명한다.
- 실제 Windows 빌드 후 `Source.zip`, 실행 패키지, SHA-256, 빌드 환경과 검증 보고서를 산출한다. Linux에서 소스/모의시험만 수행했다면 Windows 실행 완료로 쓰지 않는다.
- 앱 종료는 자기 창·worker·핸들·단축키만 정리한다. Excel/Explorer는 종료하지 않는다.
- 삭제는 앱 종료 → 시작프로그램 바로가기 해제 → 배포 폴더 삭제로 가능하게 한다. 기록 DB는 유지한다. DB 삭제는 사용자가 데이터 폴더에서 별도로 수행할 수 있게 안내한다.
- 외부 업로드, 저장소 신규 생성, 공개 배포, 서명 인증서 구매 등은 이 명세 작성만으로 수행하지 않는다. Codex가 작업할 저장소가 이미 지정되어 있으면 그 정책을 따른다.

### 성능 목표와 측정

기준 환경: Windows 11 x64, SSD, RAM 16GB 이상, 저장된 로컬 테스트 문서, 정상 응답 중인 대상 앱. OS/Office 빌드와 장비 사양을 결과에 기록한다.

| 항목 | 목표 |
|---|---|
| 최근 목록 표시 | 앱 상주 상태, 기록 1,000개에서 p95 300ms 이내 |
| 로컬 정상 캡처 커밋 | 50회 측정 p95 1.5초 이내 |
| 유휴 CPU | 안정화 후 60초 평균 전체 CPU의 1% 미만 |
| 유휴 메모리 | 전체 자체 프로세스 Private Bytes 합계 150 MiB 이하 목표 |
| 누적 누수 | 캡처/재개 100회 후 프로세스·핸들·COM 참조 무한 증가 없음 |

수치 미달 시 측정값과 원인을 보고하고 개선한다. 문서 크기·네트워크·DRM의 응답 시간을 앱 성능으로 숨기지 않는다. 광범위한 주기 폴링·화면 캡처·OCR·AI 분석은 사용하지 않는다.

## 9. 후속 웹 모듈의 경계

v1 완료 조건에 포함하지 않는다. 본체에 가짜 웹 버튼이나 미완성 등록 코드를 넣지 않는다.

후속 설계 기본값은 Manifest V3 확장, `activeTab`과 `nativeMessaging`, 사용자의 확장 버튼/단축키 요청 시에만 활성 탭 제목·URL 전달이다. DOM 본문·쿠키·비밀번호·방문 기록·탭 묶음을 읽지 않는다. [S14]

Native Messaging 호스트는 Chrome/Edge별 사용자 범위 등록, 허용 확장 ID 제한, 해제 절차를 별도 제공한다. 본체만 압축 해제하는 배포와 확장 설치는 구분한다. 회사 정책으로 차단되면 지원 불가로 안내한다. [S15]

웹 모듈은 `http`/`https`만 처리하는 방향으로 별도 명세화한다. URL query/fragment에는 화면 상태나 민감한 토큰이 포함될 수 있으므로 임의 제거도, 무조건 안전한 기록이라는 주장도 하지 않는다. URL 저장·삭제 기준을 모듈 착수 때 확정한다.

## 10. 변경 통제와 완료의 뜻

v1 기능 추가보다 정확한 대상 획득과 작은 조작을 우선한다. 업무/프로젝트 등록, 태그, 일정, 진행률, AI 요약, 자동 화면 기록, 문서 복사, 여러 파일 묶음은 추가하지 않는다.

개발 순서와 시험 ID는 `02_DELIVERY_PLAN_AND_ACCEPTANCE.md`를 따른다. 선행 PoC가 실패해도 다른 독립 작업은 계속할 수 있지만, 해당 기능을 필수 지원 완료로 표시하거나 명세에서 조용히 빼지 않는다. 해당 환경에서 v1 정식 완료는 보류하고 제한된 시험판으로 표시한다.

**최종 완료:** 필수 시험 통과 + 실제 Windows 실행 증거 + 실행 패키지 재현 빌드 + 1페이지 사용 안내 + 남은 미검증 환경의 명시. UI가 그럴듯하거나 Mock이 통과한 것만으로 완료하지 않는다.

## 11. 공식 근거

아래 자료는 2026-09-18 확인했다. API 기능의 근거이며 이 제품의 구현·업무 PC 호환성 시험 결과가 아니다. 활성 탭 매핑, 어댑터 결합 방식, 시간 예산, 중복 정책은 본 명세의 설계 판단이다.

- [S1 — RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey): 전역 단축키 등록·충돌·반복 억제.
- [S2 — GetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow), [SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow): 현재 창과 전면 전환 제한.
- [S3 — ShellFolderView.Folder](https://learn.microsoft.com/en-us/windows/win32/shell/shellfolderview-folder), [SelectedItems](https://learn.microsoft.com/en-us/windows/win32/shell/shellfolderview-selecteditems), [IShellWindows](https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nn-exdisp-ishellwindows), [QueryActiveShellView](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellbrowser-queryactiveshellview): Shell 폴더·선택·창·뷰 인터페이스.
- [S4 — ShellExecuteW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecutew): 파일 열기 요청과 반환값의 범위.
- [S5 — SHOpenFolderAndSelectItems](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shopenfolderandselectitems): 탐색기에서 파일 위치 표시.
- [S6 — AccessibleObjectFromWindow](https://learn.microsoft.com/en-us/windows/win32/api/oleacc/nf-oleacc-accessibleobjectfromwindow): Office 네이티브 객체 모델 연결.
- [S7 — Application.ActiveCell](https://learn.microsoft.com/en-us/office/vba/api/excel.application.activecell), [Workbook.FullName](https://learn.microsoft.com/en-us/office/vba/api/excel.workbook.fullname), [Workbook.Path](https://learn.microsoft.com/en-us/office/vba/api/excel.workbook.path): 활성 셀과 문서 식별 정보.
- [S8 — Workbook.Saved](https://learn.microsoft.com/en-us/office/vba/api/excel.workbook.saved): 미저장 상태 확인과 대입의 영향.
- [S9 — Application.Goto](https://learn.microsoft.com/en-us/office/vba/api/excel.application.goto): Range로 이동.
- [S10 — Workbooks.Open](https://learn.microsoft.com/en-us/office/vba/api/excel.workbooks.open), [Application.AutomationSecurity](https://learn.microsoft.com/en-us/office/vba/api/excel.application.automationsecurity): 프로그램 방식 열기와 매크로 동작.
- [S11 — Worksheet.SelectionChange](https://learn.microsoft.com/en-us/office/vba/api/excel.worksheet.selectionchange): 셀 선택 변경 이벤트.
- [S12 — .NET 지원 정책](https://dotnet.microsoft.com/en-us/platform/support/policy): 런타임 선정 근거.
- [S13 — .NET 단일 파일 배포](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview): 배포 방식의 선택지와 제약.
- [S14 — Chrome activeTab](https://developer.chrome.com/docs/extensions/develop/concepts/activeTab): 사용자 동작 시 탭 제목·URL 접근.
- [S15 — Chrome Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging), [Edge Native Messaging](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging): 호스트 등록과 확장 통신.
- [S16 — Office 스레딩 지원](https://learn.microsoft.com/en-us/visualstudio/vsto/threading-support-in-office?view=vs-2022): Office 객체 모델의 STA·Busy 처리 제약. 이 문서의 외부 worker 구성은 이를 고려한 별도 설계다.
