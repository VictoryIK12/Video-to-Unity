# 구현 방식과 선택 근거

조사일: 2026-10-27. 결론은 **VideoPlayer의 디코더 + 직접 텍스처 샘플링 + 합성 셰이더 + 재사용 가능한 이펙트 에셋**이다. Timeline은 이펙트의 시작·정지·시간을 제어한다. 특정 게임의 컨트롤러, 카메라 캡처, 씬 로딩 코드에는 의존하지 않는다.

## 비교한 방식

| 방식 | 알맞은 용도 | 이번 선택 |
|---|---|---|
| Unity VideoPlayer + 셰이더 | 큰 화면, 배너, 전환 장식, 수초 이상 영상 | 기본 재생기. 프로젝트에 별도 상용 디코더를 요구하지 않음 |
| H.264 MP4 + 같은 프레임에 알파 패킹 | Windows·모바일·웹에서 작은 배포 파일과 널리 지원되는 디코더를 우선할 때 | 기본 변환 출력. 왼쪽 RGB, 오른쪽 회색 알파 |
| VP8 알파 WebM | Linux, 투명 VideoClip을 Unity가 가져와 변환하도록 할 때 | 두 번째 변환 출력 |
| ProRes 4444 MOV 직접 재생 | 제작 원본, macOS 편집 환경 | 원본 보관용. Windows의 직접 재생을 전제로 하지 않음 |
| 가산 합성 / 검정 키 / 색상 키 | 이미 검정 또는 단색 배경으로 만들어진 MP4 | 실시간 셰이더 모드로 제공. 영상 재제작 없이 시험 가능 |
| Flipbook / Texture Sheet / VFX Graph | 짧고 작은 폭발·불꽃을 서로 다른 시간으로 많이 재생 | 적합한 대안. 이번 플러그인은 자동 파티클·Flipbook 변환기는 아님 |
| HAP / AVPro Video | 데스크톱에서 여러 고해상도 영상, 전문 미디어 설치 | 별도 디코더·상용 패키지·큰 파일 관리가 필요한 선택지. 기본 의존성에서 제외 |

Unity는 APIOnly 모드의 영상 텍스처를 직접 사용할 수 있게 한다. 따라서 매 프레임 별도 RenderTexture로 복사하는 단계를 기본 경로에 넣지 않았다. 하나의 Player를 여러 View가 참조하면 같은 재생 시간의 영상은 디코더 하나를 공유한다. [Unity: APIOnly](https://docs.unity3d.com/ja/current/ScriptReference/Video.VideoRenderMode.APIOnly.html), [렌더 대상](https://docs.unity3d.com/jp/current/Manual/VideoPlayer-intro.html)

Unity의 플랫폼 호환성 문서는 H.264를 주요 하드웨어 가속 선택지로 설명한다. 다만 실제 지원 해상도·프로파일·동시 디코더 수는 기기에 달려 있다. 패킹은 한 프레임의 너비를 두 배로 쓰므로 **1920×1080 원본이 3840×1080 디코딩을 요구한다**. 저사양 모바일에서는 원본 해상도를 먼저 낮춰야 할 수 있다. [Unity: 코덱 권장](https://docs.unity3d.com/es/current/Manual/VideoSources-FileCompatibility.html), [Android 제약](https://docs.unity3d.com/cn/2022.3/Manual/android-requirements-and-compatibility.html)

색과 알파를 같은 영상에 담으면 두 VideoPlayer를 따로 돌릴 때 생길 수 있는 프레임 불일치를 피할 수 있다. 이 레이아웃은 AVPro도 설명하는 방식이다. 본 구현은 그 제품의 코드나 라이브러리를 포함하지 않는다. [AVPro: Transparency](https://www.renderheads.com/content/docs/AVProVideo/articles/feature-transparency.html), [FFmpeg: alphaextract / alphamerge](https://ffmpeg.org/ffmpeg-filters.html#alphaextract)

## 영상이라고 해서 UI 전용은 아니다

영상은 시간에 따라 바뀌는 텍스처다. 화면 전체, 기존 Canvas 안, 게임 공간의 WorldSpace Canvas에서 같은 셰이더를 사용한다. 월드 이펙트는 공간에 놓인 평면으로 위치·회전·크기를 바꿀 수 있다. 3D 볼륨, 입자 시뮬레이션, 조명을 받는 입체 모델로 바뀌는 기능은 아니다.

Canvas 경로는 특정 URP Renderer Feature나 HDRP 커스텀 패스를 요구하지 않는다. 다만 **프로젝트 독립적인 구조와 모든 기기에서 검증됐다는 말은 다르다**. 실제 실행한 버전·파이프라인·플랫폼만 검증 문서에 기재한다.

## 언제 다른 방식을 쓰는가

512×512 RGBA 이미지 64장을 압축 없이 한 아틀라스에 넣으면 밉맵 제외 약 64MiB다. 같은 이펙트의 여러 인스턴스가 이 텍스처를 공유할 수 있다. 짧은 소형 이펙트를 동시에 많이 쓰는 경우에는 이 비용을 감수하고 Flipbook으로 만드는 편이 유리할 수 있다. 반면 고해상도·긴 영상 전체를 프레임 시트로 펴면 면적과 메모리가 빠르게 커진다. 이 수치는 픽셀 수 × 4바이트로 계산한 예시이며 실기기 벤치마크가 아니다. [Unity: Texture Sheet Animation](https://docs.unity3d.com/cn/6000.0/ScriptReference/ParticleSystem.TextureSheetAnimationModule.html)

HAP은 GPU 텍스처 압축 기반 재생을 목표로 하는 전문 코덱이다. 여러 고해상도 영상이 CPU 병목인 데스크톱 설치에는 조사할 가치가 있지만, 일반 게임의 모바일·웹 배포를 위한 기본 포맷으로 선택하지 않았다. [HAP 공식 설명](https://hap.video/), [개발자 자료](https://hap.video/developers), [AVPro 기능](https://www.renderheads.com/content/docs/AVProVideo-v3/articles/features.html)

## 성능 원칙

- 전환 직전에 처음 로드하지 말고 미리 Prepare한다.
- 같은 시간으로 보여줄 영상은 Player 하나와 View 여러 개로 공유한다.
- 화면에서 작은 이펙트에 4K 원본을 그대로 쓰지 않는다.
- 끝난 이펙트는 Stop 또는 비활성화로 디코더 자원을 반환한다.
- 기본 경로는 CPU 픽셀 읽기나 매 프레임 새 텍스처 생성을 하지 않는다.
- Timeline에서 매 프레임 강제로 seek하지 않는다. 시간 점프나 오차가 커졌을 때 보정한다.
- 임의의 영상을 실시간 셰이더 수식이나 3D 입자로 자동 복원하지 않는다.

Prepare는 재생 자원을 잡고 내용을 미리 준비하며, Stop은 준비 상태를 해제한다. Pause는 준비 상태를 유지한다. [Unity: Prepare](https://docs.unity3d.com/cn/6000.0/ScriptReference/Video.VideoPlayer.Prepare.html)

## 웹 및 파일 전달

Web에서는 VideoClip을 그대로 내장해 재생할 수 없으므로 StreamingAssets 또는 URL을 사용한다. 브라우저의 자동 재생 정책, CORS, 서버 MIME 및 지원 코덱도 확인해야 한다. 기본 무음 재생으로 불필요한 오디오 자동 재생 제약을 줄인다. Web 경로를 구현했다는 것만으로 모든 브라우저 검증을 마쳤다고 보지 않는다. [Unity: 웹 영상 재생](https://docs.unity3d.com/cn/6000.0/Manual/webgl-video.html), [MDN: 영상 코덱](https://developer.mozilla.org/en-US/docs/Web/Media/Guides/Formats/Video_codecs)
