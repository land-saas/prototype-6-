using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PortalShift.Editor
{
    /// <summary>Builds the saved authored scene. Never creates or regenerates scene content.</summary>
    public static class PortalProjectBuilder
    {
        [MenuItem("Portal Shift/Build Mac Preview")]
        public static void ValidateAndBuildMac()
        {
            Directory.CreateDirectory("Verification");
            int checks = PortalRuleChecks.Run();
            string output = Path.GetFullPath("Builds/Portal Shift.app");
            var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/PortalShift/Scenes/PortalShift.unity" },
                locationPathName = output,
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.None
            });
            if (result.summary.result != BuildResult.Succeeded)
                throw new Exception("Mac build failed: " + result.summary.result);
            File.WriteAllText("Verification/build.json", "{\"result\":\"Succeeded\",\"target\":\"StandaloneOSX\",\"rulesAssertions\":" + checks + ",\"bytes\":" + result.summary.totalSize + "}");
            Debug.Log("PORTAL_SHIFT_MAC_BUILD_SUCCEEDED " + output);
        }
    }
}
