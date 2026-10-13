using System.Collections.Generic;
using UnityEngine;
using VigilShim;

namespace VigilIOS
{
    // Turns raw touches (and game controllers) into the virtual mouse / keys / axes the patched game reads
    // (VigilShim.VInput).
    //
    //  Menus (cursor unlocked: main menu, pause, options, calibration): the finger is the mouse -
    //    touch = left button down at the finger, drag = drag (sliders), lift = release (click).
    //  Gameplay (cursor locked):
    //    * a finger that starts on the left part of the screen is a floating move stick (Horizontal/Vertical);
    //    * any other finger drags the view (Mouse X / Mouse Y), with the iOS look sensitivity;
    //      optionally a quick tap there is a left click;
    //    * the mouse stays at the screen centre (the game's crosshair ray uses the mouse position);
    //    * on-screen buttons (Hud) press the game's keys / mouse buttons.
    //  Touches that start on an on-screen button belong to the HUD; the log viewer and the iOS options
    //  panel take touches first while they are open.
    public static class TouchInput
    {
        sealed class Finger
        {
            public int Id;
            public enum Role { Hud, Stick, Look, Menu, Ignored }
            public Role R;
            public Vector2 Start, Last;
            public float StartTime;
            public bool Moved;
        }

        static readonly Dictionary<int, Finger> s_Fingers = new Dictionary<int, Finger>();
        static readonly List<int> s_Gone = new List<int>();
        static readonly HashSet<int> s_Seen = new HashSet<int>();
        static GameState.Context s_LastContext = GameState.Context.Menu;
        static int s_MenuFinger = -1;

        public static bool StickActive { get; private set; }
        public static Vector2 StickOrigin { get; private set; }
        public static Vector2 StickVector { get; private set; }
        public static float StickRadius => Mathf.Max(60f, Dpi * 0.45f) * Options.ButtonSizePct / 100f;
        public static string State => $"{GameState.Current} fingers={s_Fingers.Count} stick={(StickActive ? StickVector.ToString("0.00") : "-")}";

        static float Dpi => Screen.dpi > 0 ? Screen.dpi : 460f;
        static float MoveThreshold => Mathf.Max(10f, Dpi * 0.05f);

        // controller-driven menu cursor
        public static bool PadCursorVisible { get; private set; }
        static float s_PadCursorIdle;

        public static void Tick()
        {
            if (!VInput.Virtual) return;
            GameState.Update();
            var ctx = GameState.Current;
            if (ctx != s_LastContext)
            {
                // nothing stays held across a context change (e.g. stick held while the pause menu opens)
                ReleaseAll();
                s_LastContext = ctx;
            }
            bool gameplay = ctx != GameState.Context.Menu;

            TouchSim.Update();
            int n = TouchSim.Count;
            s_Seen.Clear();
            Vector2 stick = Vector2.zero;
            bool stickActive = false;
            for (int i = 0; i < n; i++)
            {
                Touch t = TouchSim.Get(i);
                s_Seen.Add(t.fingerId);
                if (t.phase == TouchPhase.Began || !s_Fingers.ContainsKey(t.fingerId))
                    Begin(t, gameplay);
                if (!s_Fingers.TryGetValue(t.fingerId, out var f)) continue;
                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                if (!f.Moved && (t.position - f.Start).magnitude > MoveThreshold) f.Moved = true;
                switch (f.R)
                {
                    case Finger.Role.Hud:
                        if (ended) Hud.EndTouch(t.fingerId);
                        break;
                    case Finger.Role.Stick:
                        if (!ended)
                        {
                            Vector2 d = (t.position - f.Start) / StickRadius;
                            if (d.magnitude > 1f)
                            {
                                // the stick origin follows a finger that goes past the rim
                                f.Start = t.position - d.normalized * StickRadius;
                                d = d.normalized;
                            }
                            // small dead zone, then full range
                            float m = d.magnitude;
                            stick = m < 0.12f ? Vector2.zero : d.normalized * Mathf.InverseLerp(0.12f, 1f, m);
                            stickActive = true;
                            StickOrigin = f.Start;
                        }
                        break;
                    case Finger.Role.Look:
                        {
                            Vector2 delta = t.position - f.Last;
                            // full screen height swipe = 100 "Mouse" units (~200 degrees with the game's default sensitivity)
                            float k = 100f / Mathf.Max(1f, Screen.height) * Options.LookSensitivity;
                            VInput.VLook += new Vector2(delta.x * k, delta.y * k * (Options.InvertY ? -1f : 1f));
                            if (ended && Options.TapToUse && !f.Moved && Time.unscaledTime - f.StartTime < 0.25f && ctx == GameState.Context.OnFoot)
                                VInput.TapMouseButton(0);
                        }
                        break;
                    case Finger.Role.Menu:
                        VInput.VMousePosition = t.position;
                        if (ended)
                        {
                            VInput.SetMouseButton(0, false);
                            s_MenuFinger = -1;
                        }
                        break;
                }
                f.Last = t.position;
                if (ended) s_Fingers.Remove(t.fingerId);
            }
            // fingers can vanish without an Ended phase (app switch, system gesture)
            s_Gone.Clear();
            foreach (var kv in s_Fingers) if (!s_Seen.Contains(kv.Key)) s_Gone.Add(kv.Key);
            foreach (var id in s_Gone)
            {
                var f = s_Fingers[id];
                if (f.R == Finger.Role.Hud) Hud.EndTouch(id);
                if (f.R == Finger.Role.Menu) { VInput.SetMouseButton(0, false); s_MenuFinger = -1; }
                s_Fingers.Remove(id);
            }

            StickActive = stickActive;
            StickVector = stick;
            bool stickContext = ctx == GameState.Context.OnFoot || ctx == GameState.Context.Forklift;
            VInput.VHorizontal = stickContext ? stick.x : 0f;
            VInput.VVertical = stickContext ? stick.y : 0f;

            Controller(ctx, gameplay);

            // locked cursor: the game's crosshair / item-follow code reads the mouse position = screen centre
            if (gameplay) VInput.VMousePosition = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);
        }

        static void Begin(Touch t, bool gameplay)
        {
            var f = new Finger { Id = t.fingerId, Start = t.position, Last = t.position, StartTime = Time.unscaledTime };
            s_Fingers[t.fingerId] = f;
            if (DebugOverlay.TryBeginTouch(t.position) || OptionsPanel.TryBeginTouch(t.position)) { f.R = Finger.Role.Ignored; return; }
            if (Hud.TryBeginTouch(t.position, t.fingerId)) { f.R = Finger.Role.Hud; return; }
            if (!gameplay)
            {
                if (s_MenuFinger >= 0) { f.R = Finger.Role.Ignored; return; }   // one mouse
                f.R = Finger.Role.Menu;
                s_MenuFinger = t.fingerId;
                VInput.VMousePosition = t.position;
                VInput.SetMouseButton(0, true);
                if (s_MenuTapLogs++ < 12) LogUiHit(t.position);
                return;
            }
            Rect safe = Screen.safeArea;
            bool stickSide = t.position.x < safe.xMin + safe.width * 0.42f;
            bool hasStick = false;
            foreach (var o in s_Fingers.Values) if (o.R == Finger.Role.Stick) hasStick = true;
            var ctx = GameState.Current;
            bool stickContext = ctx == GameState.Context.OnFoot || ctx == GameState.Context.Forklift;
            f.R = stickSide && !hasStick && stickContext ? Finger.Role.Stick : Finger.Role.Look;
        }

        static int s_MenuTapLogs;
        static readonly List<UnityEngine.EventSystems.RaycastResult> s_Hits = new List<UnityEngine.EventSystems.RaycastResult>();

        /// Diagnostics for the first menu taps: what the UI event system finds under the finger.
        static void LogUiHit(Vector2 pos)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) { Log.Info($"menu tap at {pos}: no EventSystem"); return; }
            var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = pos };
            s_Hits.Clear();
            es.RaycastAll(ped, s_Hits);
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < s_Hits.Count && i < 4; i++) names.Append(i > 0 ? ", " : "").Append(s_Hits[i].gameObject != null ? s_Hits[i].gameObject.name : "?");
            Log.Info($"menu tap at {pos} (screen {Screen.width}x{Screen.height}): module={(es.currentInputModule != null ? es.currentInputModule.GetType().Name : "none")} " +
                     $"hits={s_Hits.Count} [{names}]");
        }

        // ---- game controllers (MFi / Xbox / PlayStation) ------------------------------------------
        static int s_PadHeldMouse0, s_PadHeldMouse1;
        static readonly List<KeyCode> s_PadKeys = new List<KeyCode>();

        static void PadKey(KeyCode k, bool held)
        {
            VInput.SetKey(k, held);
            if (held) { if (!s_PadKeys.Contains(k)) s_PadKeys.Add(k); }
            else s_PadKeys.Remove(k);
        }

        static void Controller(GameState.Context ctx, bool gameplay)
        {
            Pad.Poll();
            if (!Pad.Connected)
            {
                if (s_PadKeys.Count > 0 || s_PadHeldMouse0 != 0 || s_PadHeldMouse1 != 0) ReleasePad();
                Blink.PadHeld = false;
                PadCursorVisible = false;
                return;
            }
            float dt = Time.unscaledDeltaTime;
            Vector2 ls = new Vector2(Pad.Axes[0], Pad.Axes[1]);
            Vector2 rs = new Vector2(Pad.Axes[2], Pad.Axes[3]);
            if (ls.magnitude < 0.15f) ls = Vector2.zero;
            if (rs.magnitude < 0.12f) rs = Vector2.zero;

            Blink.PadHeld = Pad.Held(Pad.LB) || Pad.Held(Pad.LT);
            Blink.PadEyesOpenHeld = Blink.AltMode && Pad.Held(Pad.Y);

            if (!gameplay)
            {
                // menus: left stick moves a cursor, A clicks, B / Menu = Escape
                if (ls != Vector2.zero || Pad.Held(Pad.A))
                {
                    PadCursorVisible = true;
                    s_PadCursorIdle = 0;
                    var p = (Vector2)VInput.VMousePosition + ls * Screen.height * 0.9f * dt;
                    VInput.VMousePosition = new Vector3(Mathf.Clamp(p.x, 0, Screen.width), Mathf.Clamp(p.y, 0, Screen.height), 0);
                }
                else if ((s_PadCursorIdle += dt) > 5f) PadCursorVisible = false;
                if (Pad.Pressed(Pad.A)) { VInput.SetMouseButton(0, true); s_PadHeldMouse0 = 1; }
                if (Pad.Released(Pad.A) && s_PadHeldMouse0 != 0) { VInput.SetMouseButton(0, false); s_PadHeldMouse0 = 0; }
                if (Pad.Pressed(Pad.Menu) || Pad.Pressed(Pad.B)) VInput.TapKey(KeyCode.Escape, 2);
                return;
            }
            PadCursorVisible = false;

            // look: full deflection = 150 degrees/s at the default sensitivity (the game doubles Mouse units)
            VInput.VLook += new Vector2(rs.x, rs.y * (Options.InvertY ? -1f : 1f)) * 75f * dt * Options.LookSensitivity;
            if (ctx == GameState.Context.OnFoot || ctx == GameState.Context.Forklift)
            {
                VInput.VHorizontal = Mathf.Clamp(VInput.VHorizontal + ls.x, -1f, 1f);
                VInput.VVertical = Mathf.Clamp(VInput.VVertical + ls.y, -1f, 1f);
            }

            bool use = Pad.Held(Pad.A) || Pad.Held(Pad.RT);
            if (use != (s_PadHeldMouse0 != 0)) { VInput.SetMouseButton(0, use || Hud.UseHeld); s_PadHeldMouse0 = use ? 1 : 0; }
            if (Pad.Pressed(Pad.Menu)) VInput.TapKey(KeyCode.Escape, 2);

            switch (ctx)
            {
                case GameState.Context.OnFoot:
                    if (Pad.Pressed(Pad.X)) VInput.TapKey(KeyCode.E, 2);
                    if (Pad.Pressed(Pad.Left) || (Pad.Pressed(Pad.Y) && !Blink.AltMode)) VInput.TapKey(KeyCode.LeftArrow, 2);
                    if (Pad.Pressed(Pad.Right) || Pad.Pressed(Pad.RB)) VInput.TapKey(KeyCode.RightArrow, 2);
                    break;
                case GameState.Context.Forklift:
                    {
                        bool lower = Pad.Held(Pad.RB);
                        if (lower != (s_PadHeldMouse1 != 0)) { VInput.SetMouseButton(1, lower); s_PadHeldMouse1 = lower ? 1 : 0; }
                        PadKey(KeyCode.LeftShift, Pad.Held(Pad.Up));
                        PadKey(KeyCode.LeftControl, Pad.Held(Pad.Down));
                        if (Pad.Pressed(Pad.B)) VInput.TapKey(KeyCode.Escape, 2);
                    }
                    break;
                case GameState.Context.Minigame:
                    if (Pad.Pressed(Pad.Up) || (ls.y > 0.6f && !s_StickLatch)) VInput.TapKey(KeyCode.W, 2);
                    if (Pad.Pressed(Pad.Down) || (ls.y < -0.6f && !s_StickLatch)) VInput.TapKey(KeyCode.S, 2);
                    if (Pad.Pressed(Pad.Left) || (ls.x < -0.6f && !s_StickLatch)) VInput.TapKey(KeyCode.A, 2);
                    if (Pad.Pressed(Pad.Right) || (ls.x > 0.6f && !s_StickLatch)) VInput.TapKey(KeyCode.D, 2);
                    s_StickLatch = ls.magnitude > 0.6f;
                    if (Pad.Pressed(Pad.B)) VInput.TapKey(KeyCode.Escape, 2);
                    break;
            }
        }
        static bool s_StickLatch;

        static void ReleasePad()
        {
            foreach (var k in s_PadKeys) VInput.SetKey(k, false);
            s_PadKeys.Clear();
            if (s_PadHeldMouse0 != 0) VInput.SetMouseButton(0, Hud.UseHeld);
            if (s_PadHeldMouse1 != 0) VInput.SetMouseButton(1, false);
            s_PadHeldMouse0 = s_PadHeldMouse1 = 0;
        }

        /// Lifts every virtual button and stick (context change, app in background).
        public static void ReleaseAll()
        {
            // fingers still on the glass do nothing until lifted (a finger that pressed PAUSE must not
            // become a click in the pause menu that opens under it)
            foreach (var kv in s_Fingers)
            {
                if (kv.Value.R == Finger.Role.Hud) Hud.EndTouch(kv.Key);
                kv.Value.R = Finger.Role.Ignored;
            }
            s_MenuFinger = -1;
            s_PadKeys.Clear();
            s_PadHeldMouse0 = s_PadHeldMouse1 = 0;
            Hud.ReleaseAll();
            VInput.ReleaseAll();
            StickActive = false;
            StickVector = Vector2.zero;
            Blink.ButtonHeld = false;
            Blink.EyesOpenHeld = false;
        }
    }
}
