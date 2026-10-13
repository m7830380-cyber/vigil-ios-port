using System;
using System.Collections.Generic;
using UnityEngine;
using VigilShim;

namespace VigilIOS
{
    // The iOS options panel (opened with the "iOS OPTIONS" button in the main menu / pause menu).
    // Each row: tap the left half to go down / the right half to go up (on/off rows toggle).
    // Changes apply immediately and are saved to Documents/Vigil-settings.txt.
    public class OptionsPanel : MonoBehaviour
    {
        sealed class Row
        {
            public string Name;
            public Func<string> Value;
            public Action<int> Step;      // -1 / +1
            public Rect Screen;           // pixels, origin bottom-left
        }

        public static bool Open { get; private set; }
        static readonly List<Row> s_Rows = new List<Row>();
        static Rect s_Close, s_Panel;
        static GUIStyle s_Name, s_Value, s_Title, s_Small;
        static float s_FlashRowUntil;
        static Row s_FlashRow;

        public static void Show()
        {
            Open = true;
            Log.Info("iOS options opened");
        }

        static void Hide()
        {
            Open = false;
            Options.Save();
            Log.Info("iOS options closed: " + Options.Describe());
        }

        static string OnOff(bool b) => b ? "ON" : "OFF";
        static int Cycle(int v, int d, int min, int max) => v + d > max ? min : v + d < min ? max : v + d;

        void Awake()
        {
            void R(string name, Func<string> value, Action<int> step) => s_Rows.Add(new Row { Name = name, Value = value, Step = step });

            R("Blink with", () => Blink.ManualMode
                    ? (Blink.FellBack ? "BLINK button (Face ID unavailable - tap to retry)" : "BLINK button (hold = eyes closed)")
                    : "Face ID camera (" + FaceBlink.State + ")",
                d =>
                {
                    if (Blink.FellBack) { Blink.RetryFaceTracking(); Options.Blink = Options.BlinkSource.FaceID; return; }
                    Options.Blink = Options.Blink == Options.BlinkSource.FaceID ? Options.BlinkSource.Button : Options.BlinkSource.FaceID;
                });
            R("Look sensitivity", () => Options.LookSensitivity.ToString("0.0") + "x",
                d => Options.LookSensitivity = Mathf.Clamp(Mathf.Round((Options.LookSensitivity + d * 0.1f) * 10f) / 10f, 0.2f, 4f));
            R("Invert look up/down", () => OnOff(Options.InvertY), d => Options.InvertY = !Options.InvertY);
            R("Quick tap = USE", () => OnOff(Options.TapToUse), d => Options.TapToUse = !Options.TapToUse);
            R("Render scale", () => Options.RenderScalePct + "%", d => { Options.RenderScalePct = Mathf.Clamp(Options.RenderScalePct + d * 5, 50, 100); GraphicsTuner.Apply(); });
            R("Shadows", () => new[] { "Off", "Low", "Medium", "High (PC)" }[Options.Shadows], d => { Options.Shadows = Cycle(Options.Shadows, d, 0, 3); GraphicsTuner.Apply(); });
            R("Fog quality", () => new[] { "Low", "Medium", "High (PC)" }[Options.Fog], d => { Options.Fog = Cycle(Options.Fog, d, 0, 2); GraphicsTuner.Apply(); });
            R("Detail level", () => Options.QualityName, d =>
            {
                var names = GraphicsTuner.QualityNames;
                if (names.Length == 0) return;
                int i = Array.IndexOf(names, Options.QualityName);
                Options.QualityName = names[Cycle(i < 0 ? 0 : i, d, 0, names.Length - 1)];
                GraphicsTuner.Apply();
            });
            R("TAA + motion blur", () => OnOff(Options.TaaAndMotionBlur) + (Options.TaaAndMotionBlur ? " (slow)" : ""), d => { Options.TaaAndMotionBlur = !Options.TaaAndMotionBlur; GraphicsTuner.Apply(); });
            R("Frame rate", () => Options.TargetFps + " fps", d => { Options.TargetFps = Options.TargetFps == 60 ? 30 : 60; GraphicsTuner.Apply(); });
            R("Button size", () => Options.ButtonSizePct + "%", d => Options.ButtonSizePct = Mathf.Clamp(Options.ButtonSizePct + d * 10, 70, 150));
            R("Button opacity", () => Options.ButtonOpacityPct + "%", d => Options.ButtonOpacityPct = Mathf.Clamp(Options.ButtonOpacityPct + d * 10, 20, 100));
            R("FPS counter", () => OnOff(Options.ShowFps), d => Options.ShowFps = !Options.ShowFps);
        }

        void Update()
        {
            if (!Open) return;
            // the game closed its menu under us (e.g. resumed with a controller)
            if (GameState.Current != GameState.Context.Menu) Hide();
            Layout();
        }

        static void Layout()
        {
            Rect safe = Screen.safeArea;
            float h = Screen.height;
            float pw = Mathf.Min(safe.width * 0.9f, h * 1.6f);
            float rowH = Mathf.Min(h * 0.062f, (safe.height * 0.86f) / (s_Rows.Count + 2.2f));
            float ph = rowH * (s_Rows.Count + 2.2f);
            s_Panel = new Rect(safe.center.x - pw * 0.5f, safe.center.y - ph * 0.5f, pw, ph);
            float y = s_Panel.yMax - rowH * 1.3f;
            foreach (var r in s_Rows)
            {
                y -= rowH;
                r.Screen = new Rect(s_Panel.x + pw * 0.04f, y, pw * 0.92f, rowH * 0.92f);
            }
            s_Close = new Rect(s_Panel.xMax - rowH * 2.6f, s_Panel.yMax - rowH * 1.15f, rowH * 2.4f, rowH);
        }

        /// Touch routing: while open, the panel takes every touch.
        public static bool TryBeginTouch(Vector2 pos)
        {
            if (!Open) return false;
            if (s_Close.Contains(pos) || !s_Panel.Contains(pos)) { Hide(); return true; }
            foreach (var r in s_Rows)
            {
                if (!r.Screen.Contains(pos)) continue;
                int d = pos.x < r.Screen.center.x ? -1 : 1;
                try { r.Step(d); }
                catch (Exception e) { Log.Error("option " + r.Name + ": " + e); }
                s_FlashRow = r;
                s_FlashRowUntil = Time.unscaledTime + 0.12f;
                Options.Save();
                break;
            }
            return true;
        }

        void OnGUI()
        {
            if (!Open) return;
            GUI.depth = -100;
            float h = Screen.height;
            if (s_Name == null)
            {
                s_Name = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
                s_Value = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, wordWrap = false };
                s_Title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
                s_Small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                foreach (var s in new[] { s_Name, s_Value, s_Title, s_Small }) s.normal.textColor = Color.white;
            }
            Rect G(Rect r) => new Rect(r.x, h - r.yMax, r.width, r.height);
            float rowH = s_Rows.Count > 0 ? s_Rows[0].Screen.height : h * 0.06f;
            s_Name.fontSize = s_Value.fontSize = Mathf.RoundToInt(rowH * 0.42f);
            s_Title.fontSize = Mathf.RoundToInt(rowH * 0.55f);
            s_Small.fontSize = Mathf.RoundToInt(rowH * 0.38f);

            GUI.color = new Color(0, 0, 0, 0.88f);
            GUI.DrawTexture(G(s_Panel), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(G(new Rect(s_Panel.x + s_Panel.width * 0.04f, s_Close.y, s_Panel.width * 0.6f, s_Close.height)), "VIGIL - iOS options", s_Title);
            GUI.color = new Color(0.6f, 0.1f, 0.08f, 0.9f);
            GUI.DrawTexture(G(s_Close), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(G(s_Close), "CLOSE", s_Small);
            foreach (var r in s_Rows)
            {
                bool lit = r == s_FlashRow && Time.unscaledTime < s_FlashRowUntil;
                GUI.color = lit ? new Color(0.6f, 0.1f, 0.08f, 0.8f) : new Color(1, 1, 1, 0.07f);
                GUI.DrawTexture(G(r.Screen), Texture2D.whiteTexture);
                GUI.color = Color.white;
                var g = G(r.Screen);
                GUI.Label(new Rect(g.x + g.width * 0.02f, g.y, g.width * 0.45f, g.height), r.Name, s_Name);
                string v;
                try { v = r.Value(); } catch (Exception e) { v = "? " + e.Message; }
                GUI.Label(new Rect(g.x + g.width * 0.3f, g.y, g.width * 0.68f, g.height), "<   " + v + "   >", s_Value);
            }
        }
    }
}
