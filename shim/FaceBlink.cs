using System;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VigilShim
{
    /// <summary>
    /// Replaces the desktop blink pipeline (PBP.exe, a webcam face tracker whose stdout PipeReceiver reads)
    /// with ARKit face tracking on the TrueDepth camera (Native/VigilNative.mm).
    ///
    /// The patched PipeReceiver calls <see cref="Start"/> / <see cref="Stop"/> instead of launching and
    /// killing PBP.exe; the iOS mod polls <see cref="Poll"/> every frame and writes the result into
    /// PipeReceiver.blinkValue in PBP's format: -2 = starting/stopped, -1 = no face, otherwise eye closedness.
    /// </summary>
    public static class FaceBlink
    {
        public const string DeviceName = "Front camera (Face ID)";

        // ---- state seen by the mod ----------------------------------------------------------
        /// The game currently wants blink data (a PipeReceiver is running).
        public static bool Requested { get; private set; }
        public static int StartCount { get; private set; }
        /// Last ARKit values (0 = open, 1 = closed); -1 when unknown.
        public static float Left = -1f, Right = -1f;
        public static NativeState State = NativeState.Off;
        /// Why face tracking is unavailable (set when State == Failed / Unsupported).
        public static string FailReason = "";
        /// Held by the on-screen BLINK button / controller in manual mode.
        public static bool ManualHeld;

        public enum NativeState { Off = 0, Starting = 1, Tracking = 2, NoFace = 3, Failed = -1, Unsupported = -2 }

        // ---- native (Native/VigilNative.mm, linked into the player) ----------------------------
        [DllImport("__Internal")] static extern int VigilFace_IsSupported();
        [DllImport("__Internal")] static extern int VigilFace_Start();
        [DllImport("__Internal")] static extern void VigilFace_Stop();
        [DllImport("__Internal")] static extern int VigilFace_Poll(out float left, out float right);
        [DllImport("__Internal")] static extern IntPtr VigilFace_LastError();

        static bool NativeAvailable => Application.platform == RuntimePlatform.IPhonePlayer;

        public static bool IsSupported
        {
            get
            {
                if (!NativeAvailable) return false;
                try { return VigilFace_IsSupported() != 0; }
                catch (Exception e) { FailReason = "native plugin missing: " + e.Message; return false; }
            }
        }

        /// Set by the mod: whether face tracking should actually run (false in manual-blink mode).
        public static bool UseFaceTracking = true;

        // ---- called by the patched PipeReceiver ------------------------------------------------
        public static void Start(float cameraId)
        {
            Requested = true;
            StartCount++;
            Debug.Log($"[Vigil-iOS] blink source start (game camera id {cameraId}, face tracking {(UseFaceTracking ? "on" : "off: manual BLINK button")})");
            if (UseFaceTracking) StartNative();
        }

        public static void Stop()
        {
            if (!Requested) return;
            Requested = false;
            Debug.Log("[Vigil-iOS] blink source stop");
            StopNative();
        }

        public static void StartNative()
        {
            if (!NativeAvailable) { State = NativeState.Unsupported; FailReason = "not running on iOS"; return; }
            try
            {
                if (VigilFace_IsSupported() == 0)
                {
                    State = NativeState.Unsupported;
                    FailReason = "ARKit face tracking is not supported on this device";
                    Debug.LogWarning("[Vigil-iOS] " + FailReason);
                    return;
                }
                int r = VigilFace_Start();
                State = r == 0 ? NativeState.Starting : NativeState.Failed;
                if (r != 0)
                {
                    FailReason = NativeError();
                    Debug.LogWarning("[Vigil-iOS] face tracking failed to start: " + FailReason);
                }
            }
            catch (Exception e)
            {
                State = NativeState.Failed;
                FailReason = "native plugin error: " + e.Message;
                Debug.LogError("[Vigil-iOS] " + FailReason);
            }
        }

        public static void StopNative()
        {
            if (!NativeAvailable) return;
            try { VigilFace_Stop(); } catch (Exception e) { Debug.LogWarning("[Vigil-iOS] VigilFace_Stop: " + e.Message); }
            if (State != NativeState.Unsupported) State = NativeState.Off;
            Left = Right = -1f;
        }

        /// Reads the native tracker. Called by the mod once per frame.
        public static void Poll()
        {
            if (!NativeAvailable || State == NativeState.Unsupported || State == NativeState.Off) return;
            try
            {
                int s = VigilFace_Poll(out float l, out float r);
                var ns = (NativeState)s;
                if (ns == NativeState.Failed && State != NativeState.Failed)
                {
                    FailReason = NativeError();
                    Debug.LogWarning("[Vigil-iOS] face tracking failed: " + FailReason);
                }
                State = ns;
                if (ns == NativeState.Tracking) { Left = l; Right = r; }
                else { Left = Right = -1f; }
            }
            catch (Exception e)
            {
                State = NativeState.Failed;
                FailReason = "native plugin error: " + e.Message;
            }
        }

        static string NativeError()
        {
            try
            {
                var p = VigilFace_LastError();
                return p == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringAnsi(p);
            }
            catch (Exception e) { return e.Message; }
        }

        /// Blink value in PBP's format for the current state.
        public static float PbpValue
        {
            get
            {
                switch (State)
                {
                    case NativeState.Tracking: return Mathf.Clamp01((Left + Right) * 0.5f);
                    case NativeState.NoFace: return -1f;
                    default: return -2f;
                }
            }
        }

        // ---- MouseToBlink: BlinkingManager's Input.GetMouseButton(1) is redirected here ----------------
        public static bool GetManualBlink(int button) => ManualHeld;

        // ---- camera picker (CameraSelectUI / WebcamDisplay) --------------------------------------------
        static WebCamDevice[] devices;

        /// Replaces WebCamTexture.devices: a single entry, the TrueDepth camera used by ARKit.
        public static WebCamDevice[] Devices()
        {
            if (devices != null) return devices;
            object d = new WebCamDevice();
            try
            {
                var t = typeof(WebCamDevice);
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                t.GetField("m_Name", F)?.SetValue(d, DeviceName);
                t.GetField("m_Flags", F)?.SetValue(d, 1); // kFrontFacingFlag
                devices = new[] { (WebCamDevice)d };
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Vigil-iOS] could not build the camera entry: " + e.Message);
                devices = new[] { (WebCamDevice)d };
            }
            return devices;
        }

        /// Replaces `new WebCamTexture(name)` in WebcamDisplay: ARKit owns the front camera, so there is
        /// no camera preview; the mod shows a live eye-status texture in the preview box instead.
        public static WebCamTexture NewPreview(string name) => null;
        public static void PlayPreview(WebCamTexture t) { if (t != null) t.Play(); }
        public static void StopPreview(WebCamTexture t) { if (t != null) t.Stop(); }
    }
}
