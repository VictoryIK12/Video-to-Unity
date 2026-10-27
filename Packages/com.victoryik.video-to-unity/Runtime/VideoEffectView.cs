using UnityEngine;
using UnityEngine.Rendering;

namespace VideoToUnity
{
    /// <summary>Direct decoder texture to uGUI; use an Overlay or WorldSpace Canvas.</summary>
    [ExecuteAlways]
    [RequireComponent(typeof(UnityEngine.UI.RawImage))]
    [DisallowMultipleComponent]
    [AddComponentMenu("Video to Unity/Video Effect View")]
    public sealed class VideoEffectView : MonoBehaviour, UnityEngine.UI.IMaterialModifier
    {
        public VideoEffectPlayer player;
        public VideoFit fit = VideoFit.Contain;

        private UnityEngine.UI.RawImage image;
        private Material ownedMaterial;
        public Material DisplayMaterial { get { return ownedMaterial; } }

        private void OnEnable()
        {
            image = GetComponent<UnityEngine.UI.RawImage>();
            Shader shader = Resources.Load<Shader>("VideoToUnity/VideoComposite");
            if (shader == null)
            {
                Debug.LogError("Video to Unity compositing shader is missing. Reinstall the package.", this);
                enabled = false; return;
            }
            ownedMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "Video to Unity (instance)" };
            image.SetMaterialDirty();
            Refresh();
        }

        private void LateUpdate() { Refresh(); }

        public void Refresh()
        {
            if (image == null || ownedMaterial == null) return;
            Texture texture = player != null ? player.Texture : null;
            VideoEffectAsset asset = player != null ? player.effect : null;
            // Never serialize a hidden RawImage.enabled state during Editor preview or scene save.
            if (texture == null || asset == null)
            {
                ownedMaterial.SetFloat("_Opacity", 0);
                Material hidden = image.materialForRendering;
                if (hidden != null) hidden.SetFloat("_Opacity", 0);
                return;
            }
            ownedMaterial.SetTexture("_VideoTex", texture);
            ownedMaterial.SetFloat("_UseVideoTexture", 1);
            float contentAspect = asset.contentSize.x > 0 && asset.contentSize.y > 0
                ? (float)asset.contentSize.x / asset.contentSize.y
                : (float)texture.width / Mathf.Max(1, texture.height) / (asset.alphaMode == VideoAlphaMode.PackedAlpha ? 2 : 1);
            Rect rect = image.rectTransform.rect;
            float targetAspect = rect.height > 0 ? rect.width / rect.height : contentAspect;
            Vector2 scale = AspectScale(contentAspect, targetAspect, fit);
            ApplyMaterial(ownedMaterial, asset, player.EffectiveOpacity, scale);
            // MaskableGraphic may render a stencil copy; update our uniforms without overwriting its stencil state.
            Material rendered = image.materialForRendering;
            if (rendered != null && rendered != ownedMaterial)
            {
                rendered.SetTexture("_VideoTex", texture);
                rendered.SetFloat("_UseVideoTexture", 1);
                ApplyMaterial(rendered, asset, player.EffectiveOpacity, scale);
            }
        }

        public Material GetModifiedMaterial(Material baseMaterial)
        {
            if (!isActiveAndEnabled || ownedMaterial == null) return baseMaterial;
            // Preserve the preceding MaskableGraphic's stencil. No temporary material or video
            // texture is ever assigned to a serialized RawImage field, even when a scene saves.
            foreach (string property in StencilProperties)
                if (baseMaterial != null && baseMaterial.HasProperty(property))
                    ownedMaterial.SetFloat(property, baseMaterial.GetFloat(property));
            if (baseMaterial != null && baseMaterial.IsKeywordEnabled("UNITY_UI_ALPHACLIP"))
                ownedMaterial.EnableKeyword("UNITY_UI_ALPHACLIP");
            else ownedMaterial.DisableKeyword("UNITY_UI_ALPHACLIP");
            return ownedMaterial;
        }

        private static readonly string[] StencilProperties =
            { "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask", "_ColorMask" };

        public static Vector2 AspectScale(float contentAspect, float targetAspect, VideoFit fit)
        {
            if (contentAspect <= 0 || targetAspect <= 0 || fit == VideoFit.Stretch) return Vector2.one;
            float ratio = targetAspect / contentAspect;
            if (fit == VideoFit.Contain) return ratio > 1 ? new Vector2(ratio, 1) : new Vector2(1, 1 / ratio);
            return ratio > 1 ? new Vector2(1, 1 / ratio) : new Vector2(ratio, 1);
        }

        /// <summary>Bind decoded video textures to this shader without doing an extra render-texture copy.</summary>
        public static void ApplyMaterial(Material material, VideoEffectAsset asset, float instanceOpacity, Vector2 scale)
        {
            material.SetFloat("_Mode", (float)asset.alphaMode);
            material.SetFloat("_Premultiplied", asset.premultiplied ? 1 : 0);
            material.SetFloat("_Opacity", Mathf.Clamp01(asset.opacity) * Mathf.Clamp01(instanceOpacity));
            material.SetColor("_Tint", QualitySettings.activeColorSpace == ColorSpace.Linear ? asset.tint.linear : asset.tint);
            material.SetColor("_KeyColor", asset.keyColor);
            material.SetFloat("_Threshold", Mathf.Clamp01(asset.keyThreshold));
            material.SetFloat("_Softness", Mathf.Max(0.001f, asset.keySoftness));
            material.SetFloat("_FlipY", asset.flipY ? 1 : 0);
            material.SetFloat("_CorrectPackedAlphaGamma", asset.correctPackedAlphaGamma ? 1 : 0);
            material.SetVector("_AspectScale", new Vector4(scale.x, scale.y, 0, 0));
            material.SetInt("_DstBlend", asset.alphaMode == VideoAlphaMode.Additive ? (int)BlendMode.One : (int)BlendMode.OneMinusSrcAlpha);
        }

        private void OnDisable()
        {
            if (image != null)
            {
                image.SetMaterialDirty();
            }
            if (ownedMaterial != null)
            {
                if (Application.isPlaying) Destroy(ownedMaterial); else DestroyImmediate(ownedMaterial);
                ownedMaterial = null;
            }
        }
    }
}
