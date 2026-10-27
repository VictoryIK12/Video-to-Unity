# v3 프리뷰 검증

## preview.5 이름 경고 수정

Unity 2022.3.62f3와 6000.3.23f1에서 빠른 생성 워크플로 검사 **각 11개, 총 22개 통과**(실패·생략 0). 다섯 저장 모드와 드래그 입력으로 생성한 에셋의 내부 이름이 파일명과 일치하는지, 재가져오기 후에도 유지되는지, 씬 오브젝트는 원본 영상 이름을 유지하는지, Undo가 동작하는지 확인했습니다. [검사 기록](NativeValidation/asset-names-preview5.json)

아래 전체 검사 결과는 preview.4 기준입니다. preview.5에서는 이름 저장 및 Inspector 안내만 수정했으며, 전체 렌더링·성능 검사를 다시 실행한 것은 아닙니다.

## preview.4 전체 검사

대상: **3.0.0-preview.4** · 기록일: 2026-10-27.

| 검사 | 결과 | 범위 |
|---|---|---|
| 변환기 독립 검사 | **43개 통과** | 실제 FFmpeg, 전체 프레임 알파·RGB·시간·색상 헤더 비교, 취소·손상 거부 |
| 배경 분리 독립 검사 | **28개 통과** | 실제 FFmpeg, Open-GOP 탐색, 정밀 시간 단위, 기존 투명도 거부, 병렬 결과 동일성 |
| 파일 저장·취소 | **13개 통과** | 원본 보존, 원자적 저장, 덮어쓰기 거부, 취소 시 정리 |
| Built-in 네이티브 검사 | **348개 실행 통과, 실패·생략 0** | Unity 2022.3.62f3와 6000.3.23f1, Gamma/Linear, EditMode/PlayMode |
| URP/HDRP 네이티브 검사 | **각 7개 시나리오 통과** | Unity 6000.3.23f1, URP/HDRP 17.3.0, Linear, 총 56개 RGBA 패치 비교 |
| 통합 managed 컴파일 | **14회 통과, 경고·오류 0** | 두 Unity 버전의 Editor·Player 참조 구성 |
| 배포 샘플 변환 | **2회 통과** | PackedH264와 AlphaWebM, 각각 24프레임·알파 0–255, 명시적 색상 정보 |

자동 네이티브 검사는 **Windows / RTX 5080 / Direct3D11**에서 별도 프로젝트를 만들고 숨겨진 배치 프로세스로 실행했습니다. 편집 창의 시각적 확인은 별도 [Computer Use 검사](PublicRelease/UX.md)이며 이 348개에 포함하지 않습니다. 기존 게임 프로젝트는 수정하지 않았습니다. 다른 운영체제·모바일·브라우저나 독립 실행 플레이어 빌드를 인증한 결과는 아닙니다.

## 이번 버전에서 추가 확인한 동작

- 한 창에서 드래그 입력, 보수적인 추천, 시간별 미리보기, 자동 저장과 씬 추가, 한 번의 Undo를 검사했습니다.
- 단색 MP4/MOV 배경 제거, 기존 알파 WebM 보존, Additive·Opaque·PackedAlpha 모드의 실제 저장·가져오기를 모두 검사했습니다. 오래된 미리보기로 추가하지 못하고 새 영상에서 보호 영역이 초기화됩니다.
- 긴 원본 이름은 짧은 출력 경로로 저장하며, Unity가 삭제한 에셋의 GUID를 임시 보관한 경우에도 경로를 재사용하지 않습니다.
- FFmpeg ZIP 경로 이탈·중복 항목·누락된 실행 파일·체크섬 불일치·취소를 검사했습니다. Windows에서 실제 고정 배포본 다운로드와 검증, 자동 경로 연결도 별도 실행했습니다.
- 새 창의 실제 파일 선택 버튼과 씬 추가 버튼을 Computer Use로 눌렀고 생성된 이펙트의 Play Mode 재생 상태를 확인했습니다. [이번 사용성 확인](PublicRelease/QuickWorkflowUX.md)

이번 변경 후 348개 네이티브 검사, 14개 managed 컴파일, 13개 파일 수명 검사를 다시 실행했습니다. 표의 독립 변환기·배경 처리·URP/HDRP 결과는 변경하지 않은 처리 코어와 런타임에 대한 preview.3 검증 기록입니다. 이번 릴리스에서 그 별도 스위트를 다시 실행했다고 주장하지 않습니다.

## Unity에서 확인한 동작

각 Unity 버전에서 Gamma/Linear 각각 EditMode **82개**, PlayMode **5개**를 실행했습니다. 합계는 `2 × 2 × (82 + 5) = 348`입니다.

- 실제 VideoPlayer의 준비·재생·Pause/Resume·Stop·재시작, 준비 중 취소, 반복·완료 이벤트.
- 비동기 Timeline 탐색, 마지막 요청 위치, Clip In·배속, 종료 후 기존 바인딩 복원.
- 여섯 합성 모드, 실제 PackedAlpha와 NativeAlpha 가져오기·재생, 반투명 알파의 Gamma/Linear 합성.
- RawImage의 원래 material·texture·enabled 보존, 씬 저장·재로드, Undo/Redo, 기존 Canvas·월드 배치.
- 실제 월드 Canvas, Mask·RectMask2D, 한 디코더를 공유하는 여러 View, timeScale=0 재생, 비활성화 시 정리.
- MP4 분석·미리보기·취소, Packed H.264와 알파 WebM 저장·가져오기·실제 재생. 배경 투명화, 보호된 검정 그림, 밝은 테두리와 부분 알파 경계의 합성을 비교했습니다.
- UI Toolkit 생성, 수치 입력의 실제 패널 이벤트, 색상 샘플링 좌표, 64개 보호 영역 제한, 수동 프리셋과 오래된 미리보기 안내.

Screen Space Overlay는 오브젝트 생성과 설정을 검사했습니다. 최종 화면의 Overlay 픽셀은 캡처하지 않았습니다. URP/HDRP는 실제 카메라 렌더링에서 World Space·Screen Space Camera, 각 Mask·RectMask2D를 검사했습니다. 회색 알파 128은 약 0.5로 합성됐고, 불투명한 검정 그림은 보존됐습니다. URP/HDRP의 최대 채널 오차는 **0.00390625**였습니다.

[Built-in 전체 테스트 이름·결과·의존성·해시](NativeValidation/evidence.json) · [네이티브 재현 방법](NativeValidation/README.md) · [URP/HDRP 측정·설정·캡처](PipelineValidation/REPORT.md)

## 발견하고 수정한 문제

preview.1의 변환 결과에는 일부 색상 메타데이터가 빠져 있었습니다. Windows 디코더에서 빨강이 어두워졌고 Linear 합성 검사가 실패했습니다. H.264의 프레임·컨테이너·SPS에 색상 정보를 일치시켜 기록하고 검사하도록 수정했습니다. VP8은 실제 BT.601 행렬로 변환해 디코더 해석과 맞췄습니다. 수정본의 두 출력과 네이티브 Gamma/Linear 재검사가 통과했습니다.

변환 검증은 같은 YUV 값만 비교하지 않습니다. **실제로 디코딩한 RGB**, 모든 알파 픽셀, 프레임 수, 시간 간격과 길이, 각 프레임의 색상 태그를 확인합니다. 별도의 RGB 색상 패치와 잘못된 행렬·누락된 SPS를 넣은 거부 검사도 포함했습니다.

[변환기 상세 결과](../Packages/com.victoryik.video-to-unity/Editor/Conversion/Validation~/Result.md) · [변환기 기계 판독 결과](../Packages/com.victoryik.video-to-unity/Editor/Conversion/Validation~/evidence.json)

```text
python Packages/com.victoryik.video-to-unity/Editor/Conversion/Validation~/validate.py --main-fixtures Packages/com.victoryik.video-to-unity/Samples~/Basic
```

선택 인자를 빼면 자체 생성 소스의 41개 검사를 실행합니다. 위 인자를 쓰면 진단 MOV 변환 2개가 추가됩니다. Python, .NET SDK 9 이상, FFmpeg와 ffprobe는 개발 검증용이며 플러그인 사용자에게 Python이나 .NET SDK를 요구하지 않습니다. 변환은 압축을 사용하므로 원본과 무손실로 같다는 뜻은 아닙니다.

## 배경 분리 성능

동일한 1080p60 영상으로 측정한 편집용 변환은 .NET 9에서 **100.8 → 33.7초**, Unity 2022.3의 Mono 런타임을 에디터 밖에서 실행했을 때 **150.6 → 49.7초**였습니다. 게임 실행 중 배경 분석 비용은 없습니다. 이 수치는 해당 PC·영상의 측정값이며 에디터 전체 성능이나 모든 영상의 속도를 보장하지 않습니다.

병렬화를 제한했지만 속도 향상에는 메모리 비용이 있습니다. .NET 호스트는 약 69 MiB, FFmpeg 자식 프로세스 합계는 최대 약 **1.3 GiB**, 임시 저장 공간은 약 **3.5 GB**였습니다. 한 영상 전체를 메모리에 적재하지 않으며 프레임별 검증을 유지합니다. [조건·측정값·재현 검사](../Packages/com.victoryik.video-to-unity/Editor/Background/Validation~/Result.md)

## 코드 컴파일

`CompileManaged.ps1`에 Unity Data 폴더, uGUI·Timeline·Timeline.Editor DLL을 전달합니다. NUnit과 TestRunner DLL을 지정하면 테스트 코드를 포함합니다. 7개 구성은 Runtime(Editor/Player), Timeline(Editor/Player), Timeline.Editor, Editor UI/Converter, Editor 테스트 어셈블리입니다. 컴파일 검사와 위 네이티브 실행 검사는 별개입니다.

## 남은 범위

다른 GPU/API, macOS·Linux·모바일·Web, 독립 실행 빌드, HDR 표시·톤매핑, 복잡한 중첩 마스크, 대량 동시 영상의 성능은 별도로 확인해야 합니다. URP/HDRP의 NativeAlpha WebM과 Gamma 모드도 이번 실행 범위에 포함하지 않았습니다. Timeline 프레임 단위 정확성이나 역재생을 보장하지 않습니다.
