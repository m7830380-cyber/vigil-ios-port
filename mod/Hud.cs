using System;
using System.Collections.Generic;
using UnityEngine;
using VigilShim;

namespace VigilIOS
{
    // On-screen buttons for every action VIGIL has on PC, per context:
    //   on foot   USE (hold left mouse: interact / scan / drop), INTERACT (E: pick up / doors), ITEM < > (arrows),
    //             PAUSE (Esc), BLINK (hold, when the button is the blink source)
    //   forklift  LIFT (left mouse), LOWER (right mouse), TILT up/down (Shift / Ctrl), EXIT (Esc)
    //   minigame  W / A / S / D arrows, EXIT (Esc)
    //   menus     iOS OPTIONS (main menu / pause menu)
    // Semi-transparent, thumb-sized, at the edges, inside the safe area; HIDE hides them all (tap again to show).
    // Hidden in menus, cutscenes, death screens and jumpscares (except BLINK, which the game may ask for there).
    public class Hud : MonoBehaviour
    {
        enum Kind { Hold, Tap, Toggle }

        sealed class Btn
        {
            public string Label;
            public Kind Kind;
            public Action<bool> Hold;          // Hold: true on press, false on release
            public Action Tap;
            public Func<bool> Visible;
            public float W = 1f, H = 1f;       // size in button units
            public Rect Screen;                // pixels, origin bottom-left
            public int Finger = -1;
            public float FlashUntil;
            public bool Round;
        }

        static readonly List<Btn> s_All = new List<Btn>();
        public static bool Hidden;
        static int s_PressLogs;
        public static bool UseHeld { get; private set; }
        static string s_Flash;
        static float s_FlashUntil;
        static GUIStyle s_Label, s_FlashStyle;
        static Texture2D s_Disc, s_Ring;
        static Btn s_Use, s_Interact, s_ItemL, s_ItemR, s_Pause, s_Blink, s_EyesOpen, s_Hide, s_Options,
                   s_Lift, s_Lower, s_TiltUp, s_TiltDown, s_Exit, s_W, s_A, s_S, s_D;

        static bool Ctx(GameState.Context c) => GameState.Current == c;

        void Awake()
        {
            Btn B(string label, Kind kind, Func<bool> visible, Action<bool> hold = null, Action tap = null, float w = 1f, float h = 1f, bool round = true)
            {
                var b = new Btn { Label = label, Kind = kind, Visible = visible, Hold = hold, Tap = tap, W = w, H = h, Round = round };
                s_All.Add(b);
                return b;
            }
            Action<bool> Key(KeyCode k) => held => VInput.SetKey(k, held);
            Action TapKey(KeyCode k) => () => VInput.TapKey(k, 2);

            s_Use = B("USE", Kind.Hold, () => Ctx(GameState.Context.OnFoot), held => { UseHeld = held; VInput.SetMouseButton(0, held); }, w: 1.25f, h: 1.25f);
            s_Interact = B("INTERACT", Kind.Tap, () => Ctx(GameState.Context.OnFoot), tap: TapKey(KeyCode.E));
            s_ItemL = B("<", Kind.Tap, () => Ctx(GameState.Context.OnFoot), tap: TapKey(KeyCode.LeftArrow), w: 0.75f, h: 0.75f);
            s_ItemR = B(">", Kind.Tap, () => Ctx(GameState.Context.OnFoot), tap: TapKey(KeyCode.RightArrow), w: 0.75f, h: 0.75f);
            s_Pause = B("PAUSE", Kind.Tap, () => Ctx(GameState.Context.OnFoot), tap: TapKey(KeyCode.Escape), w: 1.1f, h: 0.6f, round: false);
            s_Blink = B("BLINK", Kind.Hold, () => Blink.ButtonWanted && !OptionsPanel.Open, held => Blink.ButtonHeld = held, w: 1.25f, h: 1.25f);
            s_EyesOpen = B("EYES\nOPEN", Kind.Hold, () => Blink.AltMode && !OptionsPanel.Open && !Ctx(GameState.Context.Menu), held => Blink.EyesOpenHeld = held, w: 0.95f, h: 0.95f);

            s_Lift = B("LIFT", Kind.Hold, () => Ctx(GameState.Context.Forklift), held => { UseHeld = held; VInput.SetMouseButton(0, held); }, w: 1.15f, h: 1.15f);
            s_Lower = B("LOWER", Kind.Hold, () => Ctx(GameState.Context.Forklift), held => VInput.SetMouseButton(1, held), w: 1.15f, h: 1.15f);
            s_TiltUp = B("TILT\nUP", Kind.Hold, () => Ctx(GameState.Context.Forklift), Key(KeyCode.LeftShift), w: 0.9f, h: 0.9f);
            s_TiltDown = B("TILT\nDOWN", Kind.Hold, () => Ctx(GameState.Context.Forklift), Key(KeyCode.LeftControl), w: 0.9f, h: 0.9f);
            s_Exit = B("EXIT", Kind.Tap, () => Ctx(GameState.Context.Forklift) || Ctx(GameState.Context.Minigame), tap: TapKey(KeyCode.Escape), w: 1.1f, h: 0.6f, round: false);

            s_W = B("W", Kind.Tap, () => Ctx(GameState.Context.Minigame), tap: TapKey(KeyCode.W), w: 0.9f, h: 0.9f);
            s_A = B("A", Kind.Tap, () => Ctx(GameState.Context.Minigame), tap: TapKey(KeyCode.A), w: 0.9f, h: 0.9f);
            s_S = B("S", Kind.Tap, () => Ctx(GameState.Context.Minigame), tap: TapKey(KeyCode.S), w: 0.9f, h: 0.9f);
            s_D = B("D", Kind.Tap, () => Ctx(GameState.Context.Minigame), tap: TapKey(KeyCode.D), w: 0.9f, h: 0.9f);

            s_Hide = B("HIDE", Kind.Toggle, () => !Ctx(GameState.Context.Menu) && !Ctx(GameState.Context.Cutscene), tap: () => { Hidden = !Hidden; if (Hidden) ReleaseHolds(true); }, w: 0.9f, h: 0.6f, round: false);
            s_Options = B("iOS\nOPTIONS", Kind.Tap, () => Ctx(GameState.Context.Menu) && !OptionsPanel.Open, tap: OptionsPanel.Show, w: 1.2f, h: 0.75f, round: false);
        }

        void Update()
        {
            Layout();
            // buttons that became invisible (context change) must not stay held
            foreach (var b in s_All)
                if (b.Finger >= 0 && !IsActive(b)) EndTouch(b.Finger);
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) TouchInput.ReleaseAll();
            Log.Info(paused ? "app paused (background)" : "app resumed");
            Log.Flush();
        }

        static float Unit
        {
            get
            {
                float dpi = Screen.dpi > 0 ? Screen.dpi : 460f;
                return Mathf.Clamp(dpi * 0.42f, Screen.height * 0.11f, Screen.height * 0.2f) * Options.ButtonSizePct / 100f;
            }
        }

        static void Place(Btn b, float x, float y)
        {
            float u = Unit;
            b.Screen = new Rect(x, y, b.W * u, b.H * u);
        }

        static void Layout()
        {
            Rect safe = Screen.safeArea;
            float u = Unit;
            float m = u * 0.22f, gap = u * 0.18f;
            float right = safe.xMax - m, bottom = safe.yMin + m, top = safe.yMax - m, left = safe.xMin + m;

            // right thumb: big action button bottom-right, secondary actions around it
            Place(s_Use, right - s_Use.W * u, bottom);
            Place(s_Interact, s_Use.Screen.x - gap - u, bottom + u * 0.15f);
            Place(s_ItemR, right - s_ItemR.W * u, s_Use.Screen.yMax + gap);
            Place(s_ItemL, s_ItemR.Screen.x - gap - s_ItemL.W * u, s_ItemR.Screen.y);
            // BLINK above the item buttons, right-aligned: the right thumb reaches it without leaving USE far behind
            Place(s_Blink, right - s_Blink.W * u, s_ItemR.Screen.yMax + gap * 1.5f);

            // forklift
            Place(s_Lift, right - s_Lift.W * u, bottom + s_Lower.H * u + gap);
            Place(s_Lower, right - s_Lower.W * u, bottom);
            Place(s_TiltUp, s_Lift.Screen.x - gap - s_TiltUp.W * u, s_Lift.Screen.y + (s_Lift.Screen.height - s_TiltUp.H * u) * 0.5f);
            Place(s_TiltDown, s_Lower.Screen.x - gap - s_TiltDown.W * u, s_Lower.Screen.y + (s_Lower.Screen.height - s_TiltDown.H * u) * 0.5f);

            // minigame: arrow cross bottom-right
            float cs = s_W.W * u, cx = right - cs * 2f - gap, cy = bottom;
            Place(s_S, cx, cy);
            Place(s_A, cx - cs - gap, cy + cs * 0.5f + gap * 0.5f);
            Place(s_D, cx + cs + gap, cy + cs * 0.5f + gap * 0.5f);
            Place(s_W, cx, cy + cs + gap);
            Place(s_EyesOpen, s_Blink.Screen.x - gap - s_EyesOpen.W * u, s_Blink.Screen.y + (s_Blink.Screen.height - s_EyesOpen.H * u) * 0.5f);
            if (Ctx(GameState.Context.Forklift) || Ctx(GameState.Context.Minigame))
            {
                // BLINK stays reachable left of the context buttons
                float minX = Mathf.Min(s_TiltDown.Screen.x, s_A.Screen.x);
                Place(s_Blink, minX - gap - s_Blink.W * u, bottom);
                Place(s_EyesOpen, s_Blink.Screen.x - gap - s_EyesOpen.W * u, bottom);
            }

            // top-left: pause / exit, hide; menus: iOS options
            Place(s_Pause, left, top - s_Pause.H * u);
            Place(s_Exit, left, top - s_Exit.H * u);
            Place(s_Hide, left + s_Pause.W * u + gap, top - s_Hide.H * u);
            Place(s_Options, left, top - s_Options.H * u);
        }

        static bool IsActive(Btn b)
        {
            if (!VInput.Virtual || DebugOverlay.ShowingLog) return false;
            if (!b.Visible()) return false;
            if (Hidden && b != s_Hide) return false;
            return true;
        }

        // ---- touch routing (called from TouchInput) -----------------------------------------------
        public static bool TryBeginTouch(Vector2 pos, int finger)
        {
            // slightly larger hit area than the drawing
            float pad = Unit * 0.08f;
            foreach (var b in s_All)
            {
                if (!IsActive(b) || b.Finger >= 0) continue;
                var r = new Rect(b.Screen.x - pad, b.Screen.y - pad, b.Screen.width + 2 * pad, b.Screen.height + 2 * pad);
                if (!r.Contains(pos)) continue;
                b.Finger = finger;
                b.FlashUntil = Time.unscaledTime + 0.15f;
                if (s_PressLogs++ < 40) Log.Info($"HUD {b.Label.Replace('\n', ' ')} pressed ({GameState.Current})");
                try
                {
                    if (b.Kind == Kind.Hold) b.Hold?.Invoke(true);
                    else b.Tap?.Invoke();
                }
                catch (Exception e) { Log.Error("HUD button " + b.Label + ": " + e); }
                return true;
            }
            return false;
        }

        public static void EndTouch(int finger)
        {
            foreach (var b in s_All)
            {
                if (b.Finger != finger) continue;
                b.Finger = -1;
                if (b.Kind == Kind.Hold)
                {
                    try { b.Hold?.Invoke(false); }
                    catch (Exception e) { Log.Error("HUD button " + b.Label + ": " + e); }
                }
            }
        }

        static void ReleaseHolds(bool exceptHide)
        {
            foreach (var b in s_All)
            {
                if (exceptHide && b == s_Hide) continue;
                if (b.Finger >= 0) EndTouch(b.Finger);
            }
        }

        public static void ReleaseAll()
        {
            ReleaseHolds(false);
            UseHeld = false;
        }

        /// Centre of the visible button with this label (first line, case-insensitive), for scripted PC tests.
        public static bool TryGetButtonCenter(string label, out Vector2 center)
        {
            foreach (var b in s_All)
                if (IsActive(b) && string.Equals(b.Label.Split('\n')[0], label, StringComparison.OrdinalIgnoreCase))
                {
                    center = b.Screen.center;
                    return true;
                }
            center = default;
            return false;
        }

        public static void Flash(string msg, float seconds = 1.5f)
        {
            s_Flash = msg;
            s_FlashUntil = Time.unscaledTime + seconds;
        }

        // ---- drawing -------------------------------------------------------------------------
        static Texture2D MakeDisc(int size, bool ring)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            float r = size * 0.5f;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                    float a = Mathf.Clamp01(r - d);                              // anti-aliased edge
                    if (ring) a *= Mathf.Clamp01(d - (r - size * 0.06f));        // hollow
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        void OnGUI()
        {
            if (!VInput.Virtual) return;
            if (s_Label == null)
            {
                s_Disc = MakeDisc(128, false);
                s_Ring = MakeDisc(128, true);
                s_Label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false, fontStyle = FontStyle.Bold };
                s_Label.normal.textColor = Color.white;
                s_FlashStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
                s_FlashStyle.normal.textColor = Color.white;
            }
            float h = Screen.height;
            float alpha = Options.ButtonOpacityPct / 100f;

            // move stick
            if (TouchInput.StickActive && !Hidden && !DebugOverlay.ShowingLog)
            {
                float R = TouchInput.StickRadius;
                Vector2 o = TouchInput.StickOrigin;
                GUI.color = new Color(1, 1, 1, alpha * 0.6f);
                GUI.DrawTexture(new Rect(o.x - R, h - o.y - R, 2 * R, 2 * R), s_Ring);
                Vector2 k = o + TouchInput.StickVector * R;
                float kr = R * 0.42f;
                GUI.color = new Color(1, 1, 1, alpha * 0.8f);
                GUI.DrawTexture(new Rect(k.x - kr, h - k.y - kr, 2 * kr, 2 * kr), s_Disc);
            }

            foreach (var b in s_All)
            {
                if (!IsActive(b)) continue;
                var r = new Rect(b.Screen.x, h - b.Screen.yMax, b.Screen.width, b.Screen.height);
                bool lit = b.Finger >= 0 || Time.unscaledTime < b.FlashUntil || (b == s_Hide && Hidden);
                var tex = b.Round ? s_Disc : Texture2D.whiteTexture;
                GUI.color = lit ? new Color(0.85f, 0.15f, 0.1f, Mathf.Min(1f, alpha + 0.25f)) : new Color(0.05f, 0.05f, 0.05f, alpha);
                GUI.DrawTexture(r, tex);
                GUI.color = new Color(1, 1, 1, Mathf.Min(1f, alpha + 0.35f));
                if (b.Round) GUI.DrawTexture(r, s_Ring);
                string label = b == s_Hide && Hidden ? "SHOW" : b.Label;
                int longest = 0;
                foreach (var part in label.Split('\n')) longest = Mathf.Max(longest, part.Length);
                float usable = r.width * (b.Round ? 0.8f : 0.9f);
                float size = Mathf.Min(Mathf.Min(r.width, r.height) * (longest <= 2 ? 0.42f : label.Contains("\n") ? 0.2f : 0.24f), usable / Mathf.Max(1, longest) * 1.55f);
                s_Label.fontSize = Mathf.Max(9, Mathf.RoundToInt(size));
                GUI.Label(r, label, s_Label);
            }

            // controller cursor in menus
            if (TouchInput.PadCursorVisible)
            {
                var p = VInput.VMousePosition;
                float cr = Unit * 0.12f;
                GUI.color = new Color(1, 1, 1, 0.9f);
                GUI.DrawTexture(new Rect(p.x - cr, h - p.y - cr, 2 * cr, 2 * cr), s_Disc);
            }

            if (Time.unscaledTime < s_FlashUntil && !string.IsNullOrEmpty(s_Flash))
            {
                float w = Screen.width;
                s_FlashStyle.fontSize = Mathf.RoundToInt(h * 0.03f);
                var r = new Rect(w * 0.2f, h * 0.1f, w * 0.6f, h * 0.14f);
                GUI.color = new Color(0, 0, 0, 0.6f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(r, s_Flash, s_FlashStyle);
            }
            GUI.color = Color.white;
        }
    }
}
