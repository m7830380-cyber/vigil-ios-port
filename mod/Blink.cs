using System;
using UnityEngine;
using UnityEngine.UI;
using VigilShim;

namespace VigilIOS
{
    // Feeds the game's blink input on iOS.
    //
    //  Face ID mode: ARKit's eyeBlinkLeft/Right (VigilShim.FaceBlink) are written into PipeReceiver.blinkValue
    //  in PBP.exe's format (-2 starting, -1 no face, else closedness 0..1), so the game's own calibration
    //  screen, threshold and BlinkingManager work unchanged.
    //
    //  BLINK button mode (chosen in the iOS options, or automatically when face tracking is unavailable):
    //  BlinkingManager is switched to its MouseToBlink mode, whose right-mouse read the patcher redirected to
    //  the on-screen BLINK button (hold = eyes closed). blinkValue follows the button too (1 closed, 0 open)
    //  so the calibration screen and other blinkValue readers still work.
    public static class Blink
    {
        public static bool FellBack { get; private set; }
        public static string FallbackReason = "";
        public static bool ManualMode => Options.Blink == Options.BlinkSource.Button || FellBack;
        public static bool ButtonHeld;        // on-screen BLINK button
        public static bool PadHeld;           // controller
        public static bool EyesOpenHeld;      // on-screen EYES OPEN button (game's no-webcam mode)
        public static bool PadEyesOpenHeld;
        static bool s_AltKeysSet;

        /// The player chose "Don't Use Webcam": the game auto-blinks (AlternativeBlinking) and reads B / H.
        public static bool AltMode => BlinkingManager.singleton != null && BlinkingManager.singleton.blinkEffect == BlinkingManager.BlinkEffect.AlternativeBlinking;
        static bool s_ForcedMouseToBlink;
        static BlinkingManager s_ForcedOn;
        static int s_LastStartCount;
        static int s_HeldLogs;
        static Texture2D s_Preview;
        static float s_NextPreviewScan;
        static bool s_PreviewTracking;

        public static void Init()
        {
            FaceBlink.UseFaceTracking = !ManualMode;
            Log.Info($"blink source: {(ManualMode ? "BLINK button" : "Face ID (ARKit)")}; face tracking supported={FaceBlink.IsSupported}");
        }

        /// Runs every frame before the game's Update (PreUpdate hook).
        public static void Tick()
        {
            bool held = ButtonHeld || PadHeld;
            // the game's own "Don't Use Webcam" mode: BLINK = its blink key (B), EYES OPEN = its hold-open key (H)
            if (AltMode)
            {
                VInput.SetKey(KeyCode.B, held);
                VInput.SetKey(KeyCode.H, EyesOpenHeld || PadEyesOpenHeld);
                s_AltKeysSet = true;
            }
            else if (s_AltKeysSet)
            {
                VInput.SetKey(KeyCode.B, false);
                VInput.SetKey(KeyCode.H, false);
                s_AltKeysSet = false;
            }
            if (held != FaceBlink.ManualHeld && s_HeldLogs++ < 30) Log.Info($"BLINK {(held ? "held (eyes closed)" : "released")} game={(FaceBlink.Requested ? "listening" : "idle")} effect={(BlinkingManager.singleton != null ? BlinkingManager.singleton.blinkEffect.ToString() : "none")}");
            FaceBlink.ManualHeld = held;

            // option changed at runtime, or the game (re)started its blink pipe
            bool wantFace = !ManualMode;
            if (FaceBlink.UseFaceTracking != wantFace)
            {
                FaceBlink.UseFaceTracking = wantFace;
                if (FaceBlink.Requested)
                {
                    if (wantFace) FaceBlink.StartNative(); else FaceBlink.StopNative();
                }
            }
            if (FaceBlink.StartCount != s_LastStartCount)
            {
                s_LastStartCount = FaceBlink.StartCount;
                Log.Info($"game started its blink input (#{s_LastStartCount}), source: {(ManualMode ? "BLINK button" : "Face ID")}");
            }

            if (FaceBlink.UseFaceTracking && (FaceBlink.Requested || s_PreviewTracking)) FaceBlink.Poll();

            // automatic fallback to the button when face tracking cannot work
            if (!FellBack && FaceBlink.UseFaceTracking &&
                (FaceBlink.State == FaceBlink.NativeState.Failed || FaceBlink.State == FaceBlink.NativeState.Unsupported))
            {
                FellBack = true;
                FallbackReason = string.IsNullOrEmpty(FaceBlink.FailReason) ? FaceBlink.State.ToString() : FaceBlink.FailReason;
                Log.Warn("face tracking unavailable (" + FallbackReason + ") - switching to the on-screen BLINK button");
                Hud.Flash("Face ID blink unavailable: " + FallbackReason + "\nUse the BLINK button (hold = eyes closed)", 6f);
                FaceBlink.UseFaceTracking = false;
                FaceBlink.StopNative();
            }

            if (FaceBlink.Requested)
                PipeReceiver.blinkValue = ManualMode ? (FaceBlink.ManualHeld ? 1f : 0f) : FaceBlink.PbpValue;

            ApplyBlinkEffect();
            GuardThreshold();
        }

        // Calibration with no blinks at all (or a bad face reading) leaves the game's threshold at NaN, which
        // would make blinking impossible for the rest of the session.
        static void GuardThreshold()
        {
            float t = BlinkingManager.blinkingThreshhold;
            if (!float.IsNaN(t) && t > 0.02f && t < 0.98f) return;
            float fix = ManualMode ? 0.5f : 0.4f;
            Log.Warn($"blink threshold {t} is unusable (calibration saw no blinks?) - reset to {fix}");
            Hud.Flash("Blink calibration failed - using the default sensitivity", 4f);
            BlinkingManager.blinkingThreshhold = fix;
        }

        static void ApplyBlinkEffect()
        {
            var bm = BlinkingManager.singleton;
            if (bm == null) { s_ForcedOn = null; s_ForcedMouseToBlink = false; return; }
            if (bm != s_ForcedOn) s_ForcedMouseToBlink = false;
            if (ManualMode)
            {
                if (bm.blinkEffect == BlinkingManager.BlinkEffect.NormalBlinking)
                {
                    bm.blinkEffect = BlinkingManager.BlinkEffect.MouseToBlink;
                    s_ForcedMouseToBlink = true;
                    s_ForcedOn = bm;
                    Log.Info("BlinkingManager -> MouseToBlink (BLINK button)");
                }
            }
            else if (s_ForcedMouseToBlink && bm == s_ForcedOn && bm.blinkEffect == BlinkingManager.BlinkEffect.MouseToBlink)
            {
                bm.blinkEffect = BlinkingManager.BlinkEffect.NormalBlinking;
                s_ForcedMouseToBlink = false;
                Log.Info("BlinkingManager -> NormalBlinking (Face ID)");
            }
        }

        /// The BLINK button is shown whenever the game listens for blinks and the button is the source.
        public static bool ButtonWanted => AltMode || (ManualMode && (FaceBlink.Requested || BlinkingManager.singleton != null));

        /// Lets the user retry face tracking after a fallback (from the iOS options).
        public static void RetryFaceTracking()
        {
            FellBack = false;
            FallbackReason = "";
            FaceBlink.FailReason = "";
            if (FaceBlink.State == FaceBlink.NativeState.Failed) FaceBlink.State = FaceBlink.NativeState.Off;
            Log.Info("retrying face tracking");
        }

        public static string Status()
        {
            string src = AltMode ? "GAME AUTO-BLINK (no webcam)" : ManualMode ? (FellBack ? "BUTTON (auto: " + FallbackReason + ")" : "BUTTON") : "FaceID " + FaceBlink.State;
            string lr = FaceBlink.Left >= 0 ? $"L={FaceBlink.Left:0.00} R={FaceBlink.Right:0.00}" : "L=-- R=--";
            return $"blink {src} {lr} value={PipeReceiver.blinkValue:0.00} thr={BlinkingManager.blinkingThreshhold:0.00} " +
                   $"blinking={(BlinkingManager.blinking ? "YES" : "no")} game={(FaceBlink.Requested ? "listening" : "idle")}";
        }

        // ---- camera picker preview: ARKit owns the camera, so the preview box shows the eye state -------
        public static void UpdatePreview()
        {
            if (Time.unscaledTime >= s_NextPreviewScan)
            {
                s_NextPreviewScan = Time.unscaledTime + 0.5f;
                bool shown = false;
                foreach (var wd in UnityEngine.Object.FindObjectsByType<WebcamDisplay>(FindObjectsSortMode.None))
                {
                    var img = wd.GetComponent<RawImage>();
                    if (img == null || !img.enabled || !img.isActiveAndEnabled) continue;
                    if (img.texture == null) img.texture = PreviewTexture();
                    shown |= img.texture == s_Preview;
                }
                // camera picker on screen: run face tracking so the preview shows the eyes, like a webcam would
                if (shown && !s_PreviewTracking && !ManualMode && !FaceBlink.Requested)
                {
                    s_PreviewTracking = true;
                    Log.Info("camera picker visible - starting face tracking for the preview");
                    FaceBlink.StartNative();
                }
                else if (s_PreviewTracking && (!shown || FaceBlink.Requested))
                {
                    s_PreviewTracking = false;
                    if (!FaceBlink.Requested) FaceBlink.StopNative();
                }
            }
            if (s_Preview == null) return;
            Color open = new Color(0.25f, 0.8f, 0.35f), closed = new Color(0.05f, 0.05f, 0.05f), noFace = new Color(0.7f, 0.15f, 0.1f), wait = new Color(0.4f, 0.4f, 0.4f);
            Color l, r;
            if (ManualMode) { l = r = FaceBlink.ManualHeld ? closed : open; }
            else if (FaceBlink.State == FaceBlink.NativeState.Tracking)
            {
                l = Color.Lerp(open, closed, FaceBlink.Left);
                r = Color.Lerp(open, closed, FaceBlink.Right);
            }
            else if (FaceBlink.State == FaceBlink.NativeState.NoFace) l = r = noFace;
            else l = r = wait;
            // the image is mirrored like a selfie camera: the user's right eye is on the left
            s_Preview.SetPixel(0, 0, r);
            s_Preview.SetPixel(1, 0, l);
            s_Preview.Apply(false);
        }

        static Texture2D PreviewTexture()
        {
            if (s_Preview == null)
            {
                s_Preview = new Texture2D(2, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "VigilFacePreview" };
                s_Preview.SetPixel(0, 0, Color.gray);
                s_Preview.SetPixel(1, 0, Color.gray);
                s_Preview.Apply(false);
            }
            return s_Preview;
        }
    }
}
