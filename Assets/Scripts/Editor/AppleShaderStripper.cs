// AppleShaderStripper.cs  (Editor only, Apple fork)
// Metal builds drop the single-pass stereo (VR) shader variants: no headset runs a macOS or iOS build, yet every stereo
// variant was compiled for Metal and shipped (the macOS build log: "Fragment program with STEREO_INSTANCING_ON" for the
// game's own shaders). Fewer variants: a faster build, a smaller app, less shader memory. The count goes to the build log.

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace GoF2Remake.EditorTools
{
    public class AppleShaderStripper : IPreprocessShaders, IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        static readonly HashSet<string> Stereo = new HashSet<string>
        {
            "STEREO_INSTANCING_ON", "STEREO_MULTIVIEW_ON", "STEREO_CUBEMAP_RENDER_ON", "UNITY_SINGLE_PASS_STEREO",
        };

        static int stripped;

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) => stripped = 0;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            for (int i = data.Count - 1; i >= 0; i--)
            {
                if (data[i].shaderCompilerPlatform != ShaderCompilerPlatform.Metal) continue;
                foreach (var keyword in data[i].shaderKeywordSet.GetShaderKeywords())
                {
                    if (!Stereo.Contains(keyword.name)) continue;
                    data.RemoveAt(i);
                    stripped++;
                    break;
                }
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (stripped > 0) Debug.Log($"AppleShaderStripper: {stripped} stereo (VR) shader variants stripped from the Metal build.");
        }
    }
}
