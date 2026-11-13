# WorkBookmark 0.2.21 최종 릴리스 검증

정식 릴리스: [v0.2.21](https://github.com/prozac0401/BookMark/releases/tag/v0.2.21). 배포 소스와 태그는 `45502523a0f06ee6565b7e4d0778343a6ef85a6c`이며, 이 검증 기록은 별도 문서 커밋으로 보강했습니다.

- [최종 결과](release-verification.json): 자동검사 1,218개, MSI 54개, 전체 패키지 대조와 게시·공개 다운로드 검증.
- [공식 빌드·패키지 로그](build-package.log): `scripts/build.ps1 -Package -Msi -SourceMode Commit` 실행 결과.
- [패키지 상세 검증](package-verification.json): ZIP 1,385개 파일, Source.zip 936개 파일, 소스 커밋·실행 파일 정보 버전·모든 체크섬 대조.
- [최종 게시 DLL의 다중 선택 검사](published-selection.log): 45개 통과. 전체 검사 합계에 중복 산입하지 않습니다.
- [선택 표시 렌더](selection-preview.png): 최종 게시 DLL로 선택된 폴더·루트·하위 항목과 미선택 항목을 렌더했습니다. 바탕화면 캡처나 사람의 입력 시험이 아닙니다.

전체 자동검사는 `dcaa765a7bea4a024d0d025500c88e807b2f9d62`의 제품·테스트 코드로 실행했습니다. suite별 합계를 재확인해 Core 수를 116에서 **106**으로, Core·Desktop 합계를 885에서 **875**로 고쳤습니다. 이후 커밋 `4550252`는 README와 릴리스 안내의 숫자만 수정했으며, 제품·테스트·빌드·설치 코드는 동일합니다. 최종 self-contained 게시 실행 파일과 DLL에는 `0.2.21+45502523a0f06ee6565b7e4d0778343a6ef85a6c`가 들어 있고, 소스 ZIP과 SourceSnapshot도 같은 커밋을 가리킵니다.

사람이 직접 수행한 Ctrl 다중 선택·드래그, 이번 버전의 실제 한글 IME·혼합 배율 포인터 이동, 실제 MSI 설치·업데이트·제거·재로그인과 설치된 앱의 정상 UI 진입점은 미실행입니다. 합성 네이티브 메시지·구성요소·worker 검사와 MSI 관리 추출은 이러한 실기를 대신하지 않습니다.
