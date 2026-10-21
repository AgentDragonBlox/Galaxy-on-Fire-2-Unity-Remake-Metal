// PreTransformGuard.cs
// Remake (Android, Vulkan swapchain pre-transform, Player settings "Apply display rotation during rendering"): every game
// camera that draws to the screen renders through URP's intermediate colour texture, so only URP's final blit and the UI
// draw to the pre-rotated back buffer.
// Without it URP draws straight to the back buffer when nothing else asks for the intermediate texture (render scale
// 100 % without MSAA, FSR at 100 %+), and the remake's own screen-space shaders (the hangar shadows reading the depth
// texture, the cloak and shield bubble's refraction, the classic bloom, the bar flybys' pass) work in unrotated screen
// coordinates: part of the screen stopped updating (players' reports; #45). Pre-transform was switched off for that on
// 2026-10-14, which hands the rotation to Android's compositor, a full-screen GPU pass every frame on phones whose display
// hardware can't rotate (a Dimensity 8350 dropped from a solid 60 fps to 40-60 in the Terran hangar).
// The cost: a full-resolution blit at 100 % render scale (below it, or with MSAA, URP blits already).

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace GoF2Remake.Visuals
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public sealed class PreTransformGuard : ScriptableRenderPass
    {
        static PreTransformGuard instance;
        static bool installed;

        /// <summary>Queues the guard for every screen camera while pre-transform is in use (Bootstrap).</summary>
        public static void Install()
        {
            if (installed) return;
            installed = true;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        /// <summary>Unity applies the display rotation while rendering: an Android Vulkan device with pre-transform on (URP
        /// switches it off itself for its Tile-Only renderers).</summary>
        public static bool Active =>
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan && SystemInfo.supportsSwapChainPreTransform
            && !QualitySettings.disableSwapChainPreTransform;

        static void OnBeginCamera(ScriptableRenderContext context, Camera cam)
        {
            if (cam.cameraType != CameraType.Game || cam.targetTexture != null || !Active) return;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null || data.renderType != CameraRenderType.Base || data.scriptableRenderer == null) return;
            instance ??= new PreTransformGuard();
            data.scriptableRenderer.EnqueuePass(instance);
        }

        PreTransformGuard()
        {
            renderPassEvent = RenderPassEvent.BeforeRendering;
            requiresIntermediateTexture = true;   // all it does: UniversalRenderer then renders into the intermediate texture
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) { }
    }
}
