# Video to Unity 사용법

처음 사용한다면 **Tools → Video to Unity → 영상으로 이펙트 만들기**에서 영상 드래그부터 씬 추가까지 진행하세요. [한 창 사용법](QuickWorkflow.ko.md). 아래는 세부 설정용 고급 창 안내입니다.

## 설치

Unity 2022.3 이상을 대상으로 작성했다. 실제 검증 결과는 저장소의 Verification 문서를 따른다.

Package Manager의 **Add package from git URL**에 다음 주소를 넣는다.

```text
https://github.com/VictoryIK12/Video-to-Unity.git?path=/Packages/com.victoryik.video-to-unity#v3.0.0-preview.5
```

공개 저장소이므로 별도의 저장소 접근 권한 없이 설치할 수 있다. 로컬 패키지 ZIP을 풀고 **Add package from disk**로 package.json을 선택할 수도 있다. Unity의 uGUI와 Timeline을 의존성으로 사용한다.

메뉴: **Tools → Video to Unity → 고급 → 영상 이펙트 세부 설정**

## 가장 짧은 작업 순서

1. 기존 VideoClip을 고르거나, 알파 원본을 변환한다.
2. 영상 성격에 맞는 합성 모드를 고르고 VideoEffectAsset을 저장한다.
3. 화면 전체 / 선택한 UI / 게임 공간 중 원하는 위치에 이펙트를 만든다.
4. Play Mode에서 재생한다. 반복, 색, 투명도, 속도와 키 설정을 조정한다.

검정 MP4는 먼저 Additive 또는 BlackKey를 비교한다. 실제 알파 영상은 NativeAlpha 또는 변환 결과의 PackedAlpha를 사용한다.

MP4에서 배경색을 추정해 투명 영상을 저장하려면 **Tools → Video to Unity → 고급 → MP4 배경 분리**를 사용한다. 자동 분석·원본/체커보드 미리보기·고정 보호 사각형·시간별 확인을 제공한다. 저장 후 생성된 에셋으로 기존 배치 창을 이어갈 수 있다. [배경 분리 안내](BackgroundRemoval.ko.md)

## 게임 코드에서 재생

```csharp
using UnityEngine;
using VideoToUnity;

public sealed class EffectExample : MonoBehaviour
{
    public VideoEffectPlayer player;
    public VideoEffectAsset effect;

    public void WarmUp() => player.Prepare(effect);
    public void Show() => player.Play(effect);
    public void Hide() => player.Stop();
}
```

Play는 처음부터, Pause/Resume은 현재 위치에서 정지/이어 재생한다. 기본 시계는 UnscaledGameTime이라 게임의 timeScale이 0이어도 로딩 장식을 재생할 수 있다. 영상 디코더의 준비 완료 전에는 화면을 숨겨 검정 프레임이 번쩍이는 것을 피한다.

이벤트: onPrepared, onStarted, onCompleted, onLooped, onStopped, onError. onCompleted는 루프가 아닌 영상의 자연스러운 종료에 발생한다. 코드에서 Stop한 경우와 구분된다.

## 씬 전환에 연결

화면을 완전히 가리는 시점에 Timeline Signal이나 게임 코드에서 `SceneManager.LoadSceneAsync`를 호출한다. 씬을 바꿔도 전환 영상을 유지하려면 전용 Screen Space Overlay Canvas의 **루트 오브젝트**를 `DontDestroyOnLoad`로 유지한다. 로딩을 기다려야 하면 가려진 구간에서 Pause하고, 준비가 끝난 뒤 Resume한다. 전체 화면이 가려지는 시간은 영상마다 지정해야 하며, 플러그인이 자동 판별하지 않는다.

## 같은 영상을 여러 곳에 표시

View의 Player를 같은 VideoEffectPlayer에 연결하면 디코더 하나의 텍스처를 여러 곳에서 표시할 수 있다. 서로 다른 시간의 영상이 필요하면 별도 Player가 필요하다. 많은 소형 이펙트를 각각 다른 시간으로 재생해야 한다면 [방식 비교](Architecture.ko.md)의 Flipbook 대안을 확인한다.

## 게임 공간에 배치

WorldSpace Canvas로 생성한 이펙트는 평면 객체다. 위치, 회전, 크기를 조절해 캐릭터 옆의 장식이나 배경 이펙트로 사용한다. 다른 물체와의 가림, 카메라 각도, 투명 객체의 정렬을 실제 씬에서 확인한다. 기본 이펙트는 입력을 가로채지 않는다.

## Web 및 StreamingAssets

Web에서는 에셋의 Source를 StreamingAssets 또는 URL로 설정한다. StreamingAssets의 Location에는 폴더 기준 상대 경로를 넣는다. 예: VideoToUnity/transition.mp4.

외부 URL은 대상 플랫폼에서 지원하는 코덱과 서버 설정이 필요하다. 스트리밍 프로토콜 전부를 지원하는 상용 미디어 플레이어로 간주하지 않는다.

## 오류 확인

Player.LastError와 onError에서 준비 실패 원인을 확인한다. FFmpeg를 찾지 못하면 편집 창의 실행 파일 경로를 지정한다. Packed MP4는 PackedAlpha로, 진짜 알파 WebM은 NativeAlpha로 표시한다. 태블릿·모바일에서 재생이 실패하면 패킹 후 실제 영상 크기와 코덱 프로파일부터 확인한다.
