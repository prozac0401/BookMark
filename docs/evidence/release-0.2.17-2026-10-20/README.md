# WorkBookmark 0.2.17 릴리스 검증

2026-10-20 Windows에서 스티커 버튼의 픽토그램·역할 구분과 렌더링 변경을 확인했습니다. 한 줄 배치·기본 크기·DB v8·저장·단축키 동작은 유지합니다.

## 자동검사

`scripts/build.ps1 -ArtifactsPath ./artifacts/release-0.2.17/build` **종료 코드 0**, Release 빌드 **경고·오류 0개**, 기본 자동검사 **1,027개 통과**입니다. [전체 실행 로그](build-tests.log).

| 범위 | 통과 |
|---|---:|
| Core/Storage | 76 |
| IPC Integration | 17 |
| Desktop | 608 |
| Windows adapter | 10 |
| Excel evidence verdict | 13 |
| Capture session verdict | 14 |
| Word coordinates/protection | 24 |
| Office URL/resume | 103 |
| Browser accessibility adapter | 34 |
| Notepad recovery/coordinates | 28 |
| PDF synthetic DDE transport | 28 |
| Browser native host | 27 |
| Browser/SQLite integration | 15 |
| Browser extension Node tests | 23 |
| Registration ownership mocks | 7 |

새 Desktop 검사는 둥근 배경에 중간색 픽셀이 존재하고 거친 네이티브 영역 클리핑을 쓰지 않는지, 이동·닫힌 폴더·열린 폴더가 색상뿐 아니라 모양으로도 구분되는지, 폴더 버튼이 호버·누름·키보드 포커스 상태에서도 충분한 대비를 유지하는지 확인합니다.

기존 STK22도 통과했습니다. 일반·호버·누름·포커스 상태에서 이동 아이콘의 밝은 픽셀과 진한 배경이 함께 남는지 확인하므로 배경만 그려지고 아이콘은 사라지는 회귀를 감지할 수 있습니다. 개발 중 [스티커 집중 검사](design-checks.log)는 **285개 통과**했으며, 위 기본 검사의 부분집합이므로 중복 집계하지 않습니다.

별도 [표시 포커스 구성요소 검사](show-focus-component.log) **8개 통과, 미실행 0개**를 확인했습니다. 기본 검사와 합계 **1,035개**이며, 동일 스레드의 활성 창·포커스 확인을 실제 다른 앱 Alt+Tab이나 시스템 전경 순서 검사로 확대하지 않습니다.

## 화면과 디자인 확인

최종 빌드에서 `--render-stickers`와 `--render-folders`를 실행하고 다음 이미지를 직접 확인했습니다.

- [일반 스티커](ui/sticker-default.png): 청록색 바탕의 프레임·바깥 화살표
- [펼친 폴더와 하위 스티커](ui/folders-open.png): 남색 계열 폴더·방향 꺾쇠와 자식 개수
- [버튼 상태 비교](ui/sticker-actions-design.png): 이동·폴더 닫힘/열림·호버·누름·포커스·처리 중·접근 실패
- [비교 이미지의 배율 설명](ui/sticker-actions-design.txt)

비교 이미지의 첫 열은 **96 DPI(Windows 100%)에서 실제 버튼 픽셀**입니다. 둘째·셋째 열은 동일 비트맵을 nearest-neighbor로 2배·3배 확대한 것으로, 다른 Windows 화면 배율 시험이 아닙니다. 버튼의 배경과 벡터 그림 전체를 기기 픽셀의 4배로 그린 뒤 고품질 축소하며, 상태별 이미지는 캐시하고 교체·종료 때 해제합니다. 이전의 네이티브 자식 창 Region은 사용하지 않아 둥근 가장자리의 안티앨리어싱 픽셀이 잘리지 않습니다.

픽토그램은 [Microsoft Fluent System Icons](https://github.com/microsoft/fluentui-system-icons)의 Open·Folder·Folder Open 20 Regular를 사용했습니다. 원본 커밋은 `83b26348c811f072429427ff48362238656524fe`이며 MIT 라이선스 전문과 출처를 `Assets/FluentSystemIcons.LICENSE.txt`에 포함해 빌드·게시 결과에도 복사합니다. 작은 데스크톱 버튼의 대비를 위해 벡터 외곽에 광학적 두께를 보완했습니다.

`DrawToBitmap`은 DWM이 합성하는 창 외곽 모서리·그림자를 재현하지 않습니다. 이미지의 사각 네이티브 프레임을 실제 창 외곽선의 증거로 삼지 않습니다. 이번에는 구성요소 렌더링과 메시지·이벤트 기반 검사를 수행했으며 실제 마우스·키보드 입력, 한글 IME, 다른 앱 Alt+Tab과 여러 배율의 모니터 간 조작은 **NOT RUN**입니다.

## 패키지 검증

검사 후 소스·문서·기록을 커밋한 깨끗한 작업 트리에서 `scripts/build.ps1 -SkipTests -Package -Msi -ArtifactsPath ./artifacts/release-0.2.17/build`로 최종 배포 파일을 생성합니다. MSI 구조 검사·관리 추출과 전체 파일 SHA-256 대조는 생략하지 않습니다. 최종 소스 커밋은 `SourceSnapshot.json`, MSI 결과는 `WorkBookmark-0.2.17-win-x64.validation.json`, 배포 파일 지문은 `SHA256SUMS.txt`를 기준으로 합니다.

실제 MSI 설치·업데이트·제거·재부팅과 실사용 프로필 실행은 **NOT RUN**이며 패키지는 미서명입니다. 관리 추출은 실제 설치를 수행하지 않습니다.
