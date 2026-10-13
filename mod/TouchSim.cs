using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VigilShim;

namespace VigilIOS
{
    // Touch source for TouchInput. On the device: the real touches. On the PC (testing without a phone):
    //  -simulate-touch        the mouse plays the fingers: left button = one finger, right button = a second
    //                         finger fixed at the press point.
    //  -touch-script <file>   scripted fingers, no real mouse involved. One command per line, times in seconds
    //                         since the script started, positions as fractions of the screen from the TOP-LEFT:
    //                           12.0 tap 0.5 0.4            quick tap
    //                           13.0 down 1 0.2 0.7         finger 1 down
    //                           13.5 move 1 0.25 0.6        finger 1 moves (linear over the time to its next command)
    //                           15.0 up 1                   finger 1 lifted
    //                           16.0 key E                  tap a key (virtual keyboard)
    //                           16.5 btn 4 BLINK            finger 4 down on the HUD button labelled BLINK
    //                           17.0 shot name              screenshot -> <log folder>/shots/name.png
    //                           18.0 log some text          write a marker into the log
    //                           60.0 quit
    public static class TouchSim
    {
        public static bool Enabled { get; private set; }
        static readonly List<Touch> s_Touches = new List<Touch>();
        static bool s_LeftDown, s_RightDown;
        static Vector2 s_PrevA, s_RightPos;

        // script
        sealed class Cmd { public float T; public string Op; public string[] A; }
        static List<Cmd> s_Script;
        static int s_Next;
        static float s_Start = -1;
        sealed class SFinger { public Vector2 Pos, From, To; public float T0, T1; public bool Began, Ending; }
        static readonly Dictionary<int, SFinger> s_SF = new Dictionary<int, SFinger>();
        static readonly List<int> s_Remove = new List<int>();

        public static void Init()
        {
            var args = Environment.GetCommandLineArgs();
            int si = Array.IndexOf(args, "-touch-script");
            if (si >= 0 && si + 1 < args.Length) LoadScript(args[si + 1]);
            Enabled = s_Script != null || Array.IndexOf(args, "-simulate-touch") >= 0;
            if (Enabled) Log.Info("touch simulation enabled (" + (s_Script != null ? "script, " + s_Script.Count + " commands" : "mouse") + ")");
        }

        static void LoadScript(string path)
        {
            try
            {
                s_Script = new List<Cmd>();
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 2) continue;
                    s_Script.Add(new Cmd { T = F(p[0]), Op = p[1].ToLowerInvariant(), A = p.Length > 2 ? p[2..] : new string[0] });
                }
                s_Script.Sort((a, b) => a.T.CompareTo(b.T));
            }
            catch (Exception e) { Log.Error("touch script " + path + ": " + e.Message); s_Script = null; }
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        static Vector2 P(string x, string y) => new Vector2(F(x) * Screen.width, (1f - F(y)) * Screen.height);

        public static int Count => Enabled ? s_Touches.Count : Input.touchCount;
        public static Touch Get(int i) => Enabled ? s_Touches[i] : Input.GetTouch(i);

        /// Builds this frame's simulated touches (call once per frame before reading).
        public static void Update()
        {
            if (!Enabled) return;
            s_Touches.Clear();
            if (s_Script != null) { UpdateScript(); return; }
            Vector2 m = Input.mousePosition;
            bool left = Input.GetMouseButton(0);
            if (left || s_LeftDown) s_Touches.Add(Make(0, m, Phase(s_LeftDown, left), m - s_PrevA));
            if (left || s_LeftDown) s_PrevA = m;
            s_LeftDown = left;

            bool right = Input.GetMouseButton(1);
            if (right && !s_RightDown) s_RightPos = m;
            if (right || s_RightDown) s_Touches.Add(Make(10, s_RightPos, Phase(s_RightDown, right), Vector2.zero));
            s_RightDown = right;
        }

        static void UpdateScript()
        {
            if (s_Start < 0) s_Start = Time.realtimeSinceStartup;
            float now = Time.realtimeSinceStartup - s_Start;
            // desktop UI ignores input while the window is unfocused; scripted PC tests run in the background
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && !es.isFocused) es.SendMessage("OnApplicationFocus", true, SendMessageOptions.DontRequireReceiver);
            // fingers lifted last frame are gone now
            s_Remove.Clear();
            foreach (var kv in s_SF) if (kv.Value.Ending) s_Remove.Add(kv.Key);
            foreach (var id in s_Remove) s_SF.Remove(id);

            while (s_Next < s_Script.Count && s_Script[s_Next].T <= now)
            {
                var c = s_Script[s_Next++];
                try { Run(c, now); }
                catch (Exception e) { Log.Error($"touch script line '{c.Op} {string.Join(" ", c.A)}': {e.Message}"); }
            }
            foreach (var kv in s_SF)
            {
                var f = kv.Value;
                Vector2 prev = f.Pos;
                if (f.T1 > f.T0) f.Pos = Vector2.Lerp(f.From, f.To, Mathf.InverseLerp(f.T0, f.T1, now));
                var phase = f.Began ? TouchPhase.Began : f.Ending ? TouchPhase.Ended : (f.Pos != prev ? TouchPhase.Moved : TouchPhase.Stationary);
                s_Touches.Add(Make(kv.Key, f.Pos, phase, f.Pos - prev));
                f.Began = false;
            }
        }

        static void Run(Cmd c, float now)
        {
            switch (c.Op)
            {
                case "tap":
                    {
                        // down now, up two frames later (via a short move window)
                        var f = new SFinger { Pos = P(c.A[0], c.A[1]), Began = true };
                        f.From = f.To = f.Pos;
                        s_SF[90 + (s_Next % 5)] = f;
                        s_Script.Insert(s_Next, new Cmd { T = now + 0.08f, Op = "up", A = new[] { (90 + (s_Next % 5)).ToString() } });
                        break;
                    }
                case "down":
                    {
                        var f = new SFinger { Pos = P(c.A[1], c.A[2]), Began = true };
                        f.From = f.To = f.Pos;
                        s_SF[int.Parse(c.A[0])] = f;
                        break;
                    }
                case "move":
                    {
                        if (!s_SF.TryGetValue(int.Parse(c.A[0]), out var f)) break;
                        f.From = f.Pos;
                        f.To = P(c.A[1], c.A[2]);
                        f.T0 = now;
                        float t1 = now + 0.3f;
                        for (int i = s_Next; i < s_Script.Count; i++)
                            if (s_Script[i].A.Length > 0 && s_Script[i].A[0] == c.A[0]) { t1 = s_Script[i].T; break; }
                        f.T1 = t1;
                        break;
                    }
                case "btn":
                    {
                        // btn <finger> <LABEL>: finger down on a HUD button (lift it with "up <finger>")
                        if (!Hud.TryGetButtonCenter(c.A[1], out var center)) { Log.Warn("touch script: no visible HUD button " + c.A[1]); break; }
                        var f = new SFinger { Pos = center, Began = true };
                        f.From = f.To = f.Pos;
                        s_SF[int.Parse(c.A[0])] = f;
                        break;
                    }
                case "up":
                    if (s_SF.TryGetValue(int.Parse(c.A[0]), out var u)) u.Ending = true;
                    break;
                case "key":
                    VInput.TapKey((KeyCode)Enum.Parse(typeof(KeyCode), c.A[0], true), 2);
                    break;
                case "shot":
                    {
                        string dir = Path.Combine(Path.GetDirectoryName(Log.FilePath) ?? ".", "shots");
                        Directory.CreateDirectory(dir);
                        ScreenCapture.CaptureScreenshot(Path.Combine(dir, c.A[0] + ".png"));
                        Log.Info("screenshot " + c.A[0] + " | " + GameState.Current + " | " + Blink.Status());
                        break;
                    }
                case "log":
                    Log.Info("SCRIPT: " + string.Join(" ", c.A) + " | ctx=" + GameState.Current + " cursor=" + VCursor.LockState);
                    break;
                case "quit":
                    Log.Info("touch script finished");
                    Application.Quit();
                    break;
            }
        }

        static TouchPhase Phase(bool was, bool now) =>
            now && !was ? TouchPhase.Began : !now ? TouchPhase.Ended : TouchPhase.Moved;

        static Touch Make(int id, Vector2 pos, TouchPhase phase, Vector2 delta) =>
            new Touch { fingerId = id, position = pos, rawPosition = pos, phase = phase, deltaPosition = delta, tapCount = 1 };
    }
}
