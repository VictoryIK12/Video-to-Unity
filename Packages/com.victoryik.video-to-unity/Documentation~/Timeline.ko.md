# Timeline에서 영상 효과 배치하기

`Video Effect Track`을 사용하면 장면의 `VideoEffectPlayer` 한 개를 Timeline으로 제어할 수 있습니다. 각 클립에는 재사용할 `VideoEffectAsset`을 넣습니다. 화면 출력은 그 플레이어에 연결된 `VideoEffectView`가 담당하므로 화면 전체 UI, 기존 UI, 월드 공간 Canvas에서 같은 트랙을 사용할 수 있습니다.

Unity 2022.3 이상과 Timeline 패키지가 필요합니다. 이 패키지의 기준 의존성은 Timeline 1.7.6입니다.

## 처음 배치하기

1. 영상 소스와 합성 방식을 설정한 **Video Effect Asset**을 준비합니다. 일반 MP4도 Additive, Black Key, Chroma Key 또는 Opaque로 사용할 수 있습니다. 투명 영상 변환이 필요한 경우에는 먼저 변환을 완료합니다.
2. 장면에 **Video Effect Player**와 출력을 보여 줄 **Video Effect View**를 준비하고 View의 Player를 연결합니다. Timeline으로만 재생할 플레이어는 **Play On Enable**을 끕니다.
3. **Window > Sequencing > Timeline**을 엽니다. 장면의 빈 GameObject를 선택하고 **Create**로 Timeline Asset을 만들면 Playable Director가 연결됩니다. 기존 Director를 사용해도 됩니다.
4. Timeline의 **+ > Video to Unity > Video Effect Track**을 선택합니다.
5. 트랙 왼쪽 바인딩 칸에 2번의 **VideoEffectPlayer가 있는 GameObject** 또는 그 컴포넌트를 끌어 놓습니다. 출력 View나 원본 VideoClip을 연결하는 칸이 아닙니다.
6. Project 창의 **VideoEffectAsset**을 트랙의 원하는 시점으로 끌어 놓습니다. 영상 효과 클립이 만들어집니다. 또는 트랙 우클릭으로 Video Effect Clip을 추가하고 클립 Inspector의 **Effect**에 에셋을 넣습니다.
7. 클립의 시작 위치와 길이를 조절하고 Timeline을 재생합니다. **Opacity**는 해당 클립에만 적용하는 불투명도입니다. 에셋과 플레이어의 불투명도도 함께 적용됩니다.

VideoClip을 참조한 에셋은 원본 영상 길이를 새 클립의 기본 길이로 사용합니다. StreamingAssets나 URL은 편집 시 길이를 알 수 없으므로 기본 5초에서 직접 조절합니다. Effect를 나중에 교체해도 이미 편집한 클립 길이는 자동으로 바꾸지 않습니다.

## 시간과 속도

| 설정 | 의미 |
| --- | --- |
| Start | Timeline에서 효과가 시작되는 시점 |
| Duration | 효과가 활성화되는 Timeline 구간 |
| Clip In | 영상의 어느 시점부터 시작할지 지정하는 초 단위 값 |
| Speed Multiplier | 이 클립의 영상 진행 속도. 2이면 Timeline 1초에 영상 2초 진행 |
| Opacity | 이 클립의 불투명도. 다른 소스와의 블렌딩 기능은 아님 |

예를 들어 Start가 3초, Clip In이 1초, Speed Multiplier가 2라면 Timeline 4초에서 요청하는 영상 시점은 3초입니다. 내부적으로 Timeline이 계산한 클립 시간을 그대로 Runtime에 전달합니다. Timeline 제어 중에는 에셋의 독립 재생용 Speed 대신 Timeline의 속도 설정을 사용합니다.

일반 정방향 재생에서는 네이티브 디코더가 재생을 이어가며 Runtime이 시간 차이를 보정합니다. 스크럽과 `PlayableDirector.Evaluate()`는 재생 시작 명령이 아니라 해당 시간의 정지 화면 요청입니다. 일시 정지는 마지막 샘플을 유지하고, 클립 사이의 빈 구간·Director Stop·트랙 바인딩 변경·그래프 파괴 시에는 Timeline 제어를 해제합니다. 이전 독립 재생을 자동으로 재개하지는 않습니다.

클립의 자동 반복과 구간 밖 extrapolation은 제공하지 않습니다. 영상 길이를 넘는 시간은 Runtime이 마지막 표시 가능한 프레임으로 제한합니다. 반복이 필요하면 클립을 다시 배치합니다. 소스 교체와 반복 경계에서는 준비 지연이 생길 수 있습니다. 역방향 재생은 지원하지 않으며, 역방향 시간 평가는 정지 화면 탐색으로 처리합니다. 배속 지원 범위와 실제 동작은 Runtime과 대상 플랫폼의 디코더에 제한됩니다.

## 겹침과 페이드

**플레이어 한 개로 두 영상 소스를 동시에 디코딩하거나 크로스페이드할 수 없습니다.** 이 클립은 `ClipCaps.Blending`을 제공하지 않습니다. 같은 트랙에서 클립을 겹치지 않게 배치하세요.

스크립트나 기존 데이터 때문에 겹침이 남으면 Inspector 경고를 표시하고, 재생 시 가중치가 가장 큰 클립 하나를 선택합니다. 동률이면 믹서 입력 순서가 앞선 클립을 사용합니다. 결과는 두 영상의 혼합이 아닙니다.

두 영상을 함께 보여 주려면 **트랙·플레이어·View를 각각 두 개씩** 만들고 두 View의 Canvas 정렬 순서를 지정합니다. 각 View에 CanvasGroup을 두고 별도 Animation Track으로 Alpha를 조절하면 두 출력 사이의 페이드를 구성할 수 있습니다. 같은 플레이어를 여러 트랙에 연결하지 마세요. 잘못 연결한 경우 먼저 제어권을 얻은 트랙을 유지하고 다른 트랙에 경고합니다.

## 편집 모드 미리보기와 한계

Timeline의 Preview를 켜고 커서를 옮기면 Play Mode 밖에서도 Runtime에 샘플을 요청합니다. Player와 View의 ExecuteAlways 처리에 더해, Editor 전용 보조 코드가 미리보기 중 최대 약 30Hz로 Player Loop와 Scene View 갱신을 요청합니다. 이 보조 코드는 Director의 시간을 진행시키거나 `Evaluate`, `Play`, `Seek`를 반복 호출하지 않습니다. 클립에서 벗어나거나 Preview를 끝내 그래프가 해제되면 갱신도 끝납니다. Preview 종료 시에는 Timeline의 속성 복원과 그래프 정리 경로를 사용합니다.

미리보기 표시 여부는 코덱, 준비 완료, Unity 버전에 달려 있습니다. Unity 2022.3/6의 배치 검사에서 실제 준비·탐색·바인딩 복원을 확인했습니다. Timeline 편집 창의 시각적 미리보기는 별도로 확인해야 합니다. 영상이 늦게 보이면 재생 헤드를 잠시 멈추고 기다린 뒤, 대상 장치의 Play Mode에서도 확인하세요.

영상은 비동기로 준비·탐색됩니다. **커서를 옮기는 즉시 정확한 프레임이 보이는 기능이나 프레임 단위 동기화를 보장하지 않습니다.** 코덱·키프레임 간격·기기·네트워크 상태에 따라 탐색이 늦어질 수 있고 URL 소스는 추가 지연이 생깁니다. 프레임 정확도가 필요한 오프라인 렌더링용 영상 편집기를 대신하는 도구는 아닙니다.

## 직접 확인한 Unity 공식 자료

- [Timeline Customization Samples — Video Track](https://docs.unity.cn/Packages/com.unity.timeline@1.8/manual/samp-custom-samples.html#video-track-sample): 커스텀 트랙·클립·믹서의 구성과 영상 예제의 알려진 블렌딩·반복 한계. 이 플러그인은 샘플의 카메라 출력 대신 기존 VideoEffectPlayer/View를 사용합니다.
- [VideoPlayer.time](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Video.VideoPlayer-time.html): time 변경이 비동기 탐색을 시작하며, 진행 중 추가 탐색이 큐에 쌓일 수 있다는 설명.
- [VideoPlayer.seekCompleted](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Video.VideoPlayer-seekCompleted.html): 탐색 완료 이벤트와 코덱·인코딩 조건에 따른 탐색 지연.
- [VideoPlayer.externalReferenceTime](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Video.VideoPlayer-externalReferenceTime.html): ExternalTime 모드의 외부 기준 시계. 이 기능 자체도 즉시 정확한 프레임 표시를 보증하지 않으며, 본 트랙은 디코더 시계를 직접 변경하지 않고 Runtime에 위임합니다.
- [FrameData.effectiveSpeed](https://docs.unity3d.com/ScriptReference/Playables.FrameData-effectiveSpeed.html): 그래프를 통해 누적된 재생 속도.
- [Timeline ClipCaps](https://docs.unity.cn/Packages/com.unity.timeline@1.7/api/UnityEngine.Timeline.ClipCaps.html): Clip In, 속도 조절, 블렌딩 지원을 각각 선언하는 API.
- [EditorApplication.QueuePlayerLoopUpdate](https://docs.unity3d.com/ScriptReference/EditorApplication.QueuePlayerLoopUpdate.html): 편집 모드에서 Player Loop 갱신을 요청하는 API.
