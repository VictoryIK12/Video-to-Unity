using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace VideoToUnity.Editor
{
    public enum VideoEffectPlacement { ScreenOverlay, ExistingCanvas, WorldSpaceCanvas }

    public static class VideoEffectObjectFactory
    {
        public static VideoEffectPlayer Create(VideoEffectAsset effect, VideoEffectPlacement placement,
            Canvas parentCanvas = null, VideoFit fit = VideoFit.Contain, bool playOnEnable = true,
            float worldWidth = 2f, Vector3 worldPosition = default)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("씬 오브젝트 생성은 편집 모드에서만 가능합니다.");
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                throw new InvalidOperationException("프리팹 모드를 닫고 일반 씬에서 생성하세요.");
            if (effect == null || !AssetDatabase.Contains(effect))
                throw new ArgumentException("저장된 VideoEffectAsset을 선택하세요.");
            if (!effect.Validate(out string reason)) throw new ArgumentException(reason);
            if (!Enum.IsDefined(typeof(VideoEffectPlacement), placement)) throw new ArgumentException("잘못된 배치 방식입니다.");
            if (!IsFinite(worldWidth) || worldWidth <= 0 || !IsFinite(worldPosition.x) ||
                !IsFinite(worldPosition.y) || !IsFinite(worldPosition.z))
                throw new ArgumentException("월드 너비와 위치에 유효한 값을 입력하세요.");

            Scene scene = SceneManager.GetActiveScene();
            if (placement == VideoEffectPlacement.ExistingCanvas)
            {
                if (parentCanvas == null || EditorUtility.IsPersistent(parentCanvas) ||
                    !parentCanvas.gameObject.scene.IsValid() || !parentCanvas.gameObject.scene.isLoaded)
                    throw new ArgumentException("현재 씬의 Canvas를 선택하세요. 프리팹 에셋은 사용할 수 없습니다.");
                scene = parentCanvas.gameObject.scene;
            }
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                throw new InvalidOperationException("일반 편집 씬을 연 뒤 다시 시도하세요.");

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("영상 이펙트 만들기");
            try
            {
                var root = new GameObject(effect.name + " · Video Effect", typeof(RectTransform));
                root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, scene);
                Undo.RegisterCreatedObjectUndo(root, "영상 이펙트 루트");
                var rect = root.GetComponent<RectTransform>();
                Undo.RecordObject(rect, "영상 이펙트 크기");
                Vector2 size = DisplaySize(effect);
                rect.sizeDelta = size;
                if (placement == VideoEffectPlacement.ExistingCanvas)
                {
                    var canvasRect = parentCanvas.transform as RectTransform;
                    if (canvasRect != null && canvasRect.rect.width > 0 && canvasRect.rect.height > 0)
                        size *= Mathf.Min(1f, Mathf.Min(canvasRect.rect.width * .8f / size.x, canvasRect.rect.height * .8f / size.y));
                    Undo.SetTransformParent(rect, parentCanvas.transform, "Canvas에 영상 추가");
                    root.layer = parentCanvas.gameObject.layer;
                    rect.localRotation = Quaternion.identity;
                    rect.localScale = Vector3.one;
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
                    rect.anchoredPosition3D = Vector3.zero;
                    rect.sizeDelta = size;
                    var layout = Undo.AddComponent<LayoutElement>(root);
                    Undo.RecordObject(layout, "기존 레이아웃 유지");
                    layout.ignoreLayout = true;
                }
                else
                {
                    var canvas = Undo.AddComponent<Canvas>(root);
                    Undo.RecordObject(canvas, "영상 Canvas 설정");
                    if (placement == VideoEffectPlacement.ScreenOverlay)
                    {
                        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                        canvas.sortingOrder = 100;
                        var scaler = Undo.AddComponent<CanvasScaler>(root);
                        Undo.RecordObject(scaler, "영상 Canvas 크기 설정");
                        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                        scaler.referenceResolution = new Vector2(1920, 1080);
                        scaler.matchWidthOrHeight = .5f;
                    }
                    else
                    {
                        canvas.renderMode = RenderMode.WorldSpace;
                        rect.sizeDelta = size;
                        rect.localScale = Vector3.one * (worldWidth / size.x);
                        rect.position = worldPosition;
                        rect.rotation = Quaternion.identity;
                    }
                }
                var canvasGroup = Undo.AddComponent<CanvasGroup>(root);
                Undo.RecordObject(canvasGroup, "영상 입력 차단 해제");
                canvasGroup.blocksRaycasts = false;
                canvasGroup.interactable = false;
                var player = Undo.AddComponent<VideoEffectPlayer>(root);
                Undo.RecordObject(player, "영상 플레이어 설정");
                player.effect = effect;
                player.playOnEnable = playOnEnable;
                player.opacity = 1f;

                var child = new GameObject("Video View", typeof(RectTransform));
                child.layer = root.layer;
                Undo.RegisterCreatedObjectUndo(child, "영상 표시 영역");
                Undo.SetTransformParent(child.transform, rect, "영상 표시 영역 연결");
                var childRect = child.GetComponent<RectTransform>();
                Undo.RecordObject(childRect, "영상 표시 영역 크기");
                childRect.localRotation = Quaternion.identity;
                childRect.localScale = Vector3.one;
                childRect.anchorMin = Vector2.zero;
                childRect.anchorMax = Vector2.one;
                childRect.offsetMin = childRect.offsetMax = Vector2.zero;
                childRect.anchoredPosition3D = Vector3.zero;
                var image = Undo.AddComponent<RawImage>(child);
                Undo.RecordObject(image, "영상 표시 설정");
                image.raycastTarget = false;
                image.color = Color.white;
                var view = Undo.AddComponent<VideoEffectView>(child);
                Undo.RecordObject(view, "영상 표시 연결");
                view.player = player;
                view.fit = fit;
                Undo.RecordObject(root, "영상 이펙트 활성화");
                root.SetActive(true);
                EditorSceneManager.MarkSceneDirty(scene);
                Selection.activeGameObject = root;
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(group);
                return player;
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
        }

        internal static Vector2 DisplaySize(VideoEffectAsset effect)
        {
            float width = effect.contentSize.x;
            float height = effect.contentSize.y;
            if ((width <= 0 || height <= 0) && effect.clip != null)
            {
                width = effect.clip.width;
                height = effect.clip.height;
                if (effect.alphaMode == VideoAlphaMode.PackedAlpha) width *= .5f;
            }
            if (width <= 0 || height <= 0) return new Vector2(640, 360);
            float scale = Mathf.Min(640f / width, 360f / height);
            return new Vector2(Mathf.Max(1f, width * scale), Mathf.Max(1f, height * scale));
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
