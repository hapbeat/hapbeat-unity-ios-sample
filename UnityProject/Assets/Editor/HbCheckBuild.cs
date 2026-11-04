using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
#if UNITY_IOS
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
#endif

namespace HbCheck.EditorTools
{
    /// <summary>
    /// The iOS post-process every build runs, and entry points for building from the command line
    /// (-batchmode -executeMethod). The Build button needs none of the entry points: the settings it relies
    /// on are in ProjectSettings.
    /// </summary>
    public static class HbCheckBuild
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string LocalNetworkUsage = "Finds Hapbeat devices on the local network for the SDK check.";

#if UNITY_IOS
        /// <summary>Gives the Local Network prompt its text. An app of your own can copy this as it is.</summary>
        [PostProcessBuild]
        public static void AddLocalNetworkUsage(BuildTarget target, string xcodeProject)
        {
            if (target != BuildTarget.iOS) return;
            string path = Path.Combine(xcodeProject, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(path);
            plist.root.SetString("NSLocalNetworkUsageDescription", LocalNetworkUsage);
            plist.WriteToFile(path);
        }
#endif

        /// <summary>Throws when the committed settings would not make a working plain Build. Reads only.</summary>
        public static void CheckSettings()
        {
            NamedBuildTarget target = NamedBuildTarget.iOS;
            var problems = new List<string>();
            if (PlayerSettings.GetScriptingBackend(target) != ScriptingImplementation.IL2CPP) problems.Add("the iOS scripting backend is not IL2CPP");
            if (PlayerSettings.GetManagedStrippingLevel(target) != ManagedStrippingLevel.Minimal) problems.Add("iOS managed stripping is not Minimal");
            // The scene is empty, so engine stripping removes MonoScript (class ID 115), the default GUI skin fails to
            // load and OnGUI throws every frame.
            if (PlayerSettings.stripEngineCode) problems.Add("Strip Engine Code is on");
            if (!PlayerSettings.iOS.appleEnableAutomaticSigning) problems.Add("automatic signing is off");
            if (string.IsNullOrEmpty(PlayerSettings.GetApplicationIdentifier(target))) problems.Add("the iOS Bundle ID is empty");
            if (!Array.Exists(EditorBuildSettings.scenes, scene => scene.enabled && scene.path == ScenePath)) problems.Add(ScenePath + " is not in the build");
            if (problems.Count > 0) throw new InvalidOperationException(string.Join("; ", problems));
            Debug.Log("HB_SETTINGS ok");
        }

        /// <summary>Compile check and the reflection the Device IP field and the sent-bytes log need.</summary>
        public static void SelfTest()
        {
            HbUnicastRoute.Validate();
            Debug.Log("HB_SELFTEST ok");
        }

        /// <summary>The Build button after the one setting the README asks for. Env: HB_XCODE_DIR, HB_BUNDLE_ID.</summary>
        public static void BuildIos()
        {
            CheckSettings();
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, RequireEnv("HB_BUNDLE_ID"));
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = RequireEnv("HB_XCODE_DIR"),
                target = BuildTarget.iOS,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            Debug.Log("HB_BUILD result=" + report.summary.result + " errors=" + report.summary.totalErrors);
            if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        private static string RequireEnv(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("Environment variable " + name + " is required.");
            return value;
        }
    }
}
