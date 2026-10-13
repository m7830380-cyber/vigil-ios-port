using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.SceneManagement;
using VigilShim;

namespace VigilIOS
{
    // Entry point of the iOS additions.
    public static class Bootstrap
    {
        public static string Documents { get; private set; }
        public static bool OnDevice => Application.platform == RuntimePlatform.IPhonePlayer;

        [DllImport("__Internal")] static extern void VigilCrash_Install(string logPath);
        [DllImport("__Internal")] static extern IntPtr VigilData_Status();

        /// What VigilData.mm found in Documents/VigilData ("OK: ...", "MISSING: ...", "ERROR: ...").
        public static string DataStatus { get; private set; } = "";
        public static bool DataMissing => DataStatus.StartsWith("MISSING") || DataStatus.StartsWith("ERROR");

        // Runs before anything else in the player (before the first scene's Awake calls).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void EarlyInit()
        {
            Documents = Paths.Documents;
            Log.Init(Documents);
            try
            {
                if (OnDevice)
                {
                    Directory.SetCurrentDirectory(Documents);
                    VigilCrash_Install(Log.FilePath);
                    var p = VigilData_Status();
                    DataStatus = p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);
                }
            }
            catch (Exception e)
            {
                Log.Error("early init: " + e);
            }
            Log.Info($"VIGIL iOS starting. documents={Documents} data={Application.dataPath} streamingAssets={Application.streamingAssetsPath}");
            Log.Info($"device={SystemInfo.deviceModel} os={SystemInfo.operatingSystem} gpu={SystemInfo.graphicsDeviceName} " +
                     $"api={SystemInfo.graphicsDeviceType} mem={SystemInfo.systemMemorySize}MB vram={SystemInfo.graphicsMemorySize}MB cpu={SystemInfo.processorCount}x " +
                     $"screen={Screen.width}x{Screen.height}@{Screen.currentResolution.refreshRateRatio.value:0}Hz dpi={Screen.dpi} " +
                     $"safe={Screen.safeArea} unity={Application.unityVersion} compute={SystemInfo.supportsComputeShaders}");
            if (Log.PreviousRunEnding != null) Log.Info("previous run: " + Log.PreviousRunEnding);
            if (DataStatus.Length > 0)
            {
                if (DataMissing) Log.Error("game data: " + DataStatus);
                else Log.Info("game data: " + DataStatus);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            try
            {
                Options.Load();
                Log.Info("iOS options: " + Options.Describe());
                Input.simulateMouseWithTouches = false;
                Input.multiTouchEnabled = true;
                TouchSim.Init();
                if (TouchSim.Enabled && !OnDevice) Application.runInBackground = true;   // PC tests keep running unfocused
                VInput.Virtual = OnDevice || TouchSim.Enabled;
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
                Platform.BeforeQuit = Log.Flush;
                Blink.Init();
                GraphicsTuner.Apply();
                Log.Info($"virtual input={VInput.Virtual} targetFrameRate={Application.targetFrameRate}");

                InstallPlayerLoopHook();

                var go = new GameObject("VigilIOS");
                UnityEngine.Object.DontDestroyOnLoad(go);
                var hud = go.AddComponent<Hud>();
                go.AddComponent<OptionsPanel>();
                go.AddComponent<DebugOverlay>();
                go.AddComponent<Ticker>();
                if (DataMissing) go.AddComponent<MissingDataScreen>();

                SceneManager.sceneLoaded += (s, mode) =>
                {
                    Log.Info($"scene loaded: '{s.name}' ({mode}) objects={s.rootCount}");
                    SafeArea.OnSceneLoaded(hud);
                    GraphicsTuner.Apply();
                    Log.Flush();
                };
                Application.lowMemory += () =>
                {
                    Log.Warn($"iOS low memory warning (unity {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024)} MB) - unloading unused assets");
                    Resources.UnloadUnusedAssets();
                    Log.Flush();
                };
            }
            catch (Exception e)
            {
                Log.Error("VigilIOS init failed: " + e);
            }
        }

        // Runs at the start of every frame, after Unity has read the touches and before any MonoBehaviour.Update,
        // so the game always sees a consistent virtual mouse / keyboard / blink value.
        static void InstallPlayerLoopHook()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(PreUpdate)) continue;
                var subs = loop.subSystemList[i].subSystemList;
                var list = new PlayerLoopSystem[subs.Length + 1];
                Array.Copy(subs, list, subs.Length);
                list[subs.Length] = new PlayerLoopSystem { type = typeof(TouchInput), updateDelegate = SafeTick };
                loop.subSystemList[i].subSystemList = list;
                PlayerLoop.SetPlayerLoop(loop);
                Log.Info("input hooked into PreUpdate");
                return;
            }
            Log.Error("PreUpdate not found in player loop; touch input will not work");
        }

        static int s_TickErrors, s_BlinkErrors;
        static void SafeTick()
        {
            try { TouchInput.Tick(); }
            catch (Exception e) { if (s_TickErrors++ < 20) Log.Error("touch input: " + e); }
            try { Blink.Tick(); }
            catch (Exception e) { if (s_BlinkErrors++ < 20) Log.Error("blink: " + e); }
            VInput.BeginFrame();
        }

        // Shown when Documents/VigilData has no game data: the player is running its own empty scene.
        sealed class MissingDataScreen : MonoBehaviour
        {
            GUIStyle m_Style;
            void OnGUI()
            {
                if (DebugOverlay.ShowingLog) return;
                if (m_Style == null)
                {
                    m_Style = new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.MiddleCenter };
                    m_Style.normal.textColor = Color.white;
                }
                float w = Screen.width, h = Screen.height;
                m_Style.fontSize = Mathf.RoundToInt(h * 0.032f);
                GUI.color = Color.black;
                GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
                GUI.color = Color.white;
                Rect safe = Screen.safeArea;
                GUI.Label(new Rect(safe.xMin + w * 0.05f, h * 0.1f, safe.width - w * 0.1f, h * 0.8f),
                    "VIGIL for iOS is installed, but the game data is missing.\n\n" +
                    "Copy the folder VigilData (made on your PC) into\n" +
                    "Files / Filza  >  On My iPhone  >  VIGIL\n" +
                    "so that VIGIL/VigilData/globalgamemanagers exists, then close and reopen the app.\n\n" +
                    "(" + DataStatus + ")\n\nTap the top-right corner to see the log.", m_Style);
            }
        }

        // Per-frame work that is fine in a normal Update.
        sealed class Ticker : MonoBehaviour
        {
            float m_NextFlush;
            bool m_PreviewBroken;
            void Update()
            {
                GraphicsTuner.Tick();
                if (!m_PreviewBroken)
                    try { Blink.UpdatePreview(); } catch (Exception e) { Log.Error("blink preview: " + e); m_PreviewBroken = true; }
                if (Time.unscaledTime > m_NextFlush) { m_NextFlush = Time.unscaledTime + 5f; Log.Flush(); }
            }
        }
    }
}
