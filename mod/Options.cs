using VigilShim;

namespace VigilIOS
{
    // iOS-only options, stored in Documents/Vigil-settings.txt (VigilShim.Prefs) next to the game's own.
    public static class Options
    {
        public enum BlinkSource { FaceID = 0, Button = 1 }

        public static BlinkSource Blink;
        public static float LookSensitivity;     // multiplier, 1 = default
        public static bool InvertY;
        public static int RenderScalePct;        // 50..100
        public static int Shadows;               // 0 off, 1 low, 2 medium, 3 high (game default)
        public static int Fog;                   // 0 low, 1 medium, 2 high (game default)
        public static bool TaaAndMotionBlur;     // the game's TAA / motion blur (expensive on the phone)
        public static int TargetFps;             // 30 or 60
        public static int ButtonSizePct;         // 70..150
        public static int ButtonOpacityPct;      // 20..100
        public static bool TapToUse;             // a quick tap on the look side = left click
        public static bool ShowFps;
        public static string QualityName;        // the game's quality level (LOD detail); default High

        public static void Load()
        {
            Blink = (BlinkSource)Prefs.GetInt("ios.blink", 0);
            LookSensitivity = Prefs.GetFloat("ios.lookSensitivity", 1f);
            InvertY = Prefs.GetInt("ios.invertY", 0) != 0;
            RenderScalePct = Prefs.GetInt("ios.renderScale", 70);
            Shadows = Prefs.GetInt("ios.shadows", 1);
            Fog = Prefs.GetInt("ios.fog", 1);
            TaaAndMotionBlur = Prefs.GetInt("ios.taaMotionBlur", 0) != 0;
            TargetFps = Prefs.GetInt("ios.fps", 60);
            ButtonSizePct = Prefs.GetInt("ios.buttonSize", 100);
            ButtonOpacityPct = Prefs.GetInt("ios.buttonOpacity", 45);
            TapToUse = Prefs.GetInt("ios.tapToUse", 0) != 0;
            ShowFps = Prefs.GetInt("ios.showFps", 1) != 0;
            QualityName = Prefs.GetString("ios.quality", "High");
            Clamp();
        }

        static void Clamp()
        {
            if (Blink != BlinkSource.FaceID && Blink != BlinkSource.Button) Blink = BlinkSource.FaceID;
            LookSensitivity = UnityEngine.Mathf.Clamp(LookSensitivity, 0.2f, 4f);
            RenderScalePct = UnityEngine.Mathf.Clamp(RenderScalePct, 50, 100);
            Shadows = UnityEngine.Mathf.Clamp(Shadows, 0, 3);
            Fog = UnityEngine.Mathf.Clamp(Fog, 0, 2);
            TargetFps = TargetFps <= 30 ? 30 : 60;
            ButtonSizePct = UnityEngine.Mathf.Clamp(ButtonSizePct, 70, 150);
            ButtonOpacityPct = UnityEngine.Mathf.Clamp(ButtonOpacityPct, 20, 100);
        }

        public static void Save()
        {
            Clamp();
            Prefs.SetInt("ios.blink", (int)Blink);
            Prefs.SetFloat("ios.lookSensitivity", LookSensitivity);
            Prefs.SetInt("ios.invertY", InvertY ? 1 : 0);
            Prefs.SetInt("ios.renderScale", RenderScalePct);
            Prefs.SetInt("ios.shadows", Shadows);
            Prefs.SetInt("ios.fog", Fog);
            Prefs.SetInt("ios.taaMotionBlur", TaaAndMotionBlur ? 1 : 0);
            Prefs.SetInt("ios.fps", TargetFps);
            Prefs.SetInt("ios.buttonSize", ButtonSizePct);
            Prefs.SetInt("ios.buttonOpacity", ButtonOpacityPct);
            Prefs.SetInt("ios.tapToUse", TapToUse ? 1 : 0);
            Prefs.SetInt("ios.showFps", ShowFps ? 1 : 0);
            Prefs.SetString("ios.quality", QualityName ?? "High");
        }

        public static string Describe() =>
            $"blink={Blink} look={LookSensitivity:0.00} invertY={InvertY} renderScale={RenderScalePct}% shadows={Shadows} fog={Fog} " +
            $"taa/mblur={TaaAndMotionBlur} quality={QualityName} fps={TargetFps} buttons={ButtonSizePct}%/{ButtonOpacityPct}% tapToUse={TapToUse}";
    }
}
