using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Builds the iOS player "shell" with Unity 6000.0.26f1 (the game's exact version): an IL2CPP player that
// contains the patched retail VIGIL assemblies (Assets/Plugins/Game, from the private repo), the iOS mod,
// the native helpers (Plugins/iOS/*.mm) and the FMOD / Steam Audio iOS static libraries (Plugins/iOS/*.a,
// from the private repo). The game's own data is not in the IPA: the user copies it into Documents/VigilData,
// and VigilData.mm points the player at it.
public static class IosShellBuild
{
    const string ScenePath = "Assets/Scenes/Boot.unity";
    static readonly NamedBuildTarget Target = NamedBuildTarget.iOS;

    public static void Build()
    {
        string outDir = Environment.GetEnvironmentVariable("SHELL_OUT") ?? "Build/iOS";

        if (!File.Exists(ScenePath))
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);

        PlayerSettings.companyName = "Singularity Studios";
        PlayerSettings.productName = "VIGIL";
        PlayerSettings.SetApplicationIdentifier(Target, "com.vigil.iosport");
        PlayerSettings.bundleVersion = "1.0";
        PlayerSettings.iOS.buildNumber = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER") ?? "1";
        PlayerSettings.SetScriptingBackend(Target, ScriptingImplementation.IL2CPP);
        // link.xml keeps every game assembly whole; Minimal is the least stripping IL2CPP allows.
        PlayerSettings.SetManagedStrippingLevel(Target, ManagedStrippingLevel.Minimal);
        PlayerSettings.stripEngineCode = false;
        PlayerSettings.SetIl2CppCompilerConfiguration(Target, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetApiCompatibilityLevel(Target, ApiCompatibilityLevel.NET_Unity_4_8);
        PlayerSettings.allowUnsafeCode = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });
        PlayerSettings.colorSpace = ColorSpace.Linear;      // same as the retail game (URP)
        PlayerSettings.MTRendering = true;
        PlayerSettings.iOS.targetOSVersionString = "15.0";
        PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
        PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneAndiPad;
        PlayerSettings.iOS.requiresFullScreen = true;
        PlayerSettings.iOS.hideHomeButton = true;
        PlayerSettings.iOS.deferSystemGesturesMode = UnityEngine.iOS.SystemGestureDeferMode.All;
        PlayerSettings.iOS.cameraUsageDescription = "VIGIL uses the front (Face ID) camera to detect when you blink. No image is shown, stored or sent anywhere.";
        PlayerSettings.statusBarHidden = true;
        PlayerSettings.accelerometerFrequency = 0;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.enableInternalProfiler = false;
        PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
        PlayerSettings.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);
        PlayerSettings.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
        PlayerSettings.usePlayerLog = true;
        PlayerSettings.iOS.appleEnableAutomaticSigning = false;

        var plugins = Directory.Exists("Assets/Plugins/Game") ? Directory.GetFiles("Assets/Plugins/Game", "*.dll").Length : 0;
        var nativeLibs = Directory.Exists("Assets/Plugins/iOS") ? Directory.GetFiles("Assets/Plugins/iOS", "*.a").Select(Path.GetFileName).ToArray() : new string[0];
        Debug.Log($"[IosShellBuild] {plugins} game/mod assemblies, native libs: {string.Join(", ", nativeLibs)}");
        if (plugins == 0) Debug.LogWarning("[IosShellBuild] no game assemblies in Assets/Plugins/Game - building an empty shell");

        var opts = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = outDir,
            target = BuildTarget.iOS,
            targetGroup = BuildTargetGroup.iOS,
            options = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log("[IosShellBuild] result: " + report.summary.result + " errors=" + report.summary.totalErrors);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            // Keep the linked assemblies IL2CPP choked on (Library/Temp is wiped when the editor quits) so CI
            // can hand them to the PRIVATE repo for inspection. They contain game code: never publish them publicly.
            string dbg = Environment.GetEnvironmentVariable("SHELL_DEBUG_OUT");
            foreach (var staged in new[] { "Library/Bee/artifacts/iOS/ManagedStripped", "Temp/StagingArea/Data/Managed" })
            {
                if (string.IsNullOrEmpty(dbg) || !Directory.Exists(staged)) continue;
                Directory.CreateDirectory(dbg);
                foreach (var f in Directory.GetFiles(staged, "*.dll"))
                    File.Copy(f, Path.Combine(dbg, Path.GetFileName(f)), true);
                Debug.Log("[IosShellBuild] copied staged assemblies from " + staged + " to " + dbg);
            }
            EditorApplication.Exit(1);
        }
    }

    [PostProcessBuild(100)]
    public static void OnPostprocess(BuildTarget target, string path)
    {
        if (target != BuildTarget.iOS) return;

        // ---- Info.plist ----
        string plistPath = Path.Combine(path, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        var root = plist.root;
        root.SetString("CFBundleDisplayName", "VIGIL");
        root.SetBoolean("UIFileSharingEnabled", true);           // Documents visible in Files / Filza / Finder
        root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
        root.SetBoolean("UISupportsDocumentBrowser", true);
        root.SetBoolean("CADisableMinimumFrameDurationOnPhone", true);
        root.SetBoolean("UIRequiresFullScreen", true);
        root.SetBoolean("UIStatusBarHidden", true);
        root.SetBoolean("ITSAppUsesNonExemptEncryption", false);
        root.SetBoolean("GCSupportsControllerUserInteraction", true);
        root.SetString("NSCameraUsageDescription", PlayerSettings.iOS.cameraUsageDescription);
        var orientations = root.CreateArray("UISupportedInterfaceOrientations");
        orientations.AddString("UIInterfaceOrientationLandscapeRight");
        orientations.AddString("UIInterfaceOrientationLandscapeLeft");
        plist.WriteToFile(plistPath);

        // ---- Xcode project ----
        string projPath = PBXProject.GetPBXProjectPath(path);
        var proj = new PBXProject();
        proj.ReadFromFile(projPath);
        string main = proj.GetUnityMainTargetGuid(), fw = proj.GetUnityFrameworkTargetGuid();
        foreach (var f in new[] { "ARKit.framework", "GameController.framework", "AVFoundation.framework", "AudioToolbox.framework", "Accelerate.framework", "CoreMedia.framework" })
            proj.AddFrameworkToProject(fw, f, false);
        proj.AddBuildProperty(fw, "OTHER_LDFLAGS", "-lc++");
        foreach (var guid in new[] { main, fw })
        {
            proj.SetBuildProperty(guid, "CODE_SIGNING_ALLOWED", "NO");
            proj.SetBuildProperty(guid, "CODE_SIGNING_REQUIRED", "NO");
            proj.SetBuildProperty(guid, "CODE_SIGN_IDENTITY", "");
            proj.SetBuildProperty(guid, "IPHONEOS_DEPLOYMENT_TARGET", "15.0");
            proj.AddBuildProperty(guid, "WARNING_CFLAGS", "-Wno-error");
            proj.SetBuildProperty(guid, "GCC_TREAT_WARNINGS_AS_ERRORS", "NO");
        }
        proj.WriteToFile(projPath);

        // ---- data folder: let VigilData.mm pick Documents/VigilData ----
        string fsPath = Path.Combine(path, "Classes/Unity/Filesystem.mm");
        string fs = File.ReadAllText(fsPath);
        const string original = "dir = AllocCString([[NSBundle mainBundle] bundlePath]);";
        const string patched = "dir = AllocCString(VigilResolveDataBundleDir([[NSBundle mainBundle] bundlePath]));";
        if (!fs.Contains(patched))
        {
            if (!fs.Contains(original))
                throw new BuildFailedException("Filesystem.mm: UnityDataBundleDir() is not what IosShellBuild expects - cannot redirect the data folder");
            fs = fs.Replace(original, patched);
            fs = fs.Replace("#include \"UnityInterface.h\"", "#include \"UnityInterface.h\"\nextern \"C\" NSString* VigilResolveDataBundleDir(NSString* bundleDir);");
            File.WriteAllText(fsPath, fs);
        }
        Debug.Log("[IosShellBuild] Xcode project post-processed (plist, frameworks, unsigned, data folder redirect)");
    }
}
