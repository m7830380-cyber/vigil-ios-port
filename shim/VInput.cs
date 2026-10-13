using System;
using UnityEngine;

namespace VigilShim
{
    /// <summary>
    /// Replacement for UnityEngine.Input. The patcher rewrites every Input call in the game assemblies
    /// (Assembly-CSharp, UnityEngine.UI, TextMeshPro, GPUInstancer, ...) to the member with the same name here.
    ///
    /// While <see cref="Virtual"/> is on (set by the iOS touch layer) the mouse is the virtual one the touch
    /// layer drives, keys are the virtual ones OR the real keyboard (a Bluetooth keyboard keeps working), and
    /// axes are the real axis plus the virtual one (touch stick / drag-look / game controller).
    /// With Virtual off everything forwards to UnityEngine.Input unchanged.
    /// </summary>
    public static class VInput
    {
        public static bool Virtual;

        // ---- virtual mouse -------------------------------------------------------------------
        public static Vector3 VMousePosition;
        /// Scroll accumulated by the touch layer; becomes this frame's mouseScrollDelta at BeginFrame.
        public static Vector2 VScroll;
        /// Look delta accumulated by the touch layer / controller in "Mouse X/Y" axis units
        /// (Unity's default mouse axis = pixels * 0.1); becomes this frame's value at BeginFrame.
        public static Vector2 VLook;
        static Vector2 scrollThisFrame, lookThisFrame;
        static readonly bool[] mouseWant = new bool[3];
        static readonly bool[] mouseCur = new bool[3];
        static readonly bool[] mousePrev = new bool[3];
        static readonly int[] mouseTapFrames = new int[3];

        public static void SetMouseButton(int b, bool held) { if (b >= 0 && b < 3) mouseWant[b] = held; }
        /// Press and release a button, guaranteeing at least one frame down.
        public static void TapMouseButton(int b) { if (b >= 0 && b < 3) mouseTapFrames[b] = 1; }
        public static bool IsMouseHeld(int b) => b >= 0 && b < 3 && mouseCur[b];

        // ---- virtual axes (Horizontal / Vertical from the stick or a controller) ---------------
        public static float VHorizontal, VVertical;

        // ---- virtual keys ----------------------------------------------------------------------
        const int MaxKey = 512;
        static readonly bool[] keyWant = new bool[MaxKey];
        static readonly bool[] keyCur = new bool[MaxKey];
        static readonly bool[] keyPrev = new bool[MaxKey];
        static readonly int[] keyTapFrames = new int[MaxKey];
        static bool anyVirtualKey, anyVirtualKeyDown;

        public static void SetKey(KeyCode k, bool held)
        {
            int i = (int)k;
            if (i <= 0 || i >= MaxKey) return;
            keyWant[i] = held;
        }
        /// Press a key for <paramref name="frames"/> frames, then release it.
        public static void TapKey(KeyCode k, int frames = 1)
        {
            int i = (int)k;
            if (i <= 0 || i >= MaxKey) return;
            keyTapFrames[i] = Math.Max(keyTapFrames[i], frames);
        }
        public static bool IsVirtualKeyHeld(KeyCode k) { int i = (int)k; return i > 0 && i < MaxKey && keyCur[i]; }

        /// Releases everything the touch layer holds (used when the HUD hides or the app pauses).
        public static void ReleaseAll()
        {
            for (int b = 0; b < 3; b++) { mouseWant[b] = false; mouseTapFrames[b] = 0; }
            for (int i = 0; i < MaxKey; i++) { keyWant[i] = false; keyTapFrames[i] = 0; }
            VHorizontal = VVertical = 0;
            VLook = Vector2.zero;
        }

        public static int Frame { get; private set; }

        /// Called once per frame by the touch layer before any game Update runs: commits the wanted
        /// state so Down/Up edges are stable for the whole frame (same semantics as UnityEngine.Input).
        public static void BeginFrame()
        {
            Frame++;
            for (int b = 0; b < 3; b++)
            {
                mousePrev[b] = mouseCur[b];
                bool tap = mouseTapFrames[b] > 0;
                if (tap) mouseTapFrames[b]--;
                mouseCur[b] = mouseWant[b] || tap;
            }
            anyVirtualKey = anyVirtualKeyDown = false;
            for (int i = 1; i < MaxKey; i++)
            {
                keyPrev[i] = keyCur[i];
                bool tap = keyTapFrames[i] > 0;
                if (tap) keyTapFrames[i]--;
                keyCur[i] = keyWant[i] || tap;
                anyVirtualKey |= keyCur[i];
                anyVirtualKeyDown |= keyCur[i] && !keyPrev[i];
            }
            scrollThisFrame = VScroll;
            VScroll = Vector2.zero;
            lookThisFrame = VLook;
            VLook = Vector2.zero;
        }

        static bool VKey(int i) => i > 0 && i < MaxKey && keyCur[i];
        static bool VKeyDown(int i) => i > 0 && i < MaxKey && keyCur[i] && !keyPrev[i];
        static bool VKeyUp(int i) => i > 0 && i < MaxKey && !keyCur[i] && keyPrev[i];

        static int MouseIndex(KeyCode k) => k >= KeyCode.Mouse0 && k <= KeyCode.Mouse2 ? (int)(k - KeyCode.Mouse0) : -1;

        // UnityEngine.Input throws for axes that are not in the Input Manager; the game only uses the
        // standard ones, but never let a missing axis take a frame down.
        static float RealAxis(string axis, bool raw)
        {
            try { return raw ? Input.GetAxisRaw(axis) : Input.GetAxis(axis); }
            catch (ArgumentException) { return 0f; }
        }

        static float VirtualAxis(string axis)
        {
            switch (axis)
            {
                case "Horizontal": return VHorizontal;
                case "Vertical": return VVertical;
                case "Mouse X": return lookThisFrame.x;
                case "Mouse Y": return lookThisFrame.y;
                case "Mouse ScrollWheel": return scrollThisFrame.y * 0.1f;
                default: return 0f;
            }
        }

        // ---- UnityEngine.Input surface ---------------------------------------------------------
        public static bool GetKey(KeyCode k)
        {
            if (!Virtual) return Input.GetKey(k);
            int mb = MouseIndex(k);
            if (mb >= 0) return mouseCur[mb] || VKey((int)k);
            return VKey((int)k) || Input.GetKey(k);
        }
        public static bool GetKeyDown(KeyCode k)
        {
            if (!Virtual) return Input.GetKeyDown(k);
            int mb = MouseIndex(k);
            if (mb >= 0) return (mouseCur[mb] && !mousePrev[mb]) || VKeyDown((int)k);
            return VKeyDown((int)k) || Input.GetKeyDown(k);
        }
        public static bool GetKeyUp(KeyCode k)
        {
            if (!Virtual) return Input.GetKeyUp(k);
            int mb = MouseIndex(k);
            if (mb >= 0) return (!mouseCur[mb] && mousePrev[mb]) || VKeyUp((int)k);
            return VKeyUp((int)k) || Input.GetKeyUp(k);
        }
        public static bool GetKey(string name) => Input.GetKey(name);
        public static bool GetKeyDown(string name) => Input.GetKeyDown(name);
        public static bool GetKeyUp(string name) => Input.GetKeyUp(name);

        public static bool GetMouseButton(int b) => Virtual ? b >= 0 && b < 3 && mouseCur[b] : Input.GetMouseButton(b);
        public static bool GetMouseButtonDown(int b) => Virtual ? b >= 0 && b < 3 && mouseCur[b] && !mousePrev[b] : Input.GetMouseButtonDown(b);
        public static bool GetMouseButtonUp(int b) => Virtual ? b >= 0 && b < 3 && !mouseCur[b] && mousePrev[b] : Input.GetMouseButtonUp(b);

        public static Vector3 mousePosition => Virtual ? VMousePosition : Input.mousePosition;
        public static Vector2 mouseScrollDelta => Virtual ? scrollThisFrame : Input.mouseScrollDelta;
        public static bool mousePresent => Virtual || Input.mousePresent;
        /// Game/UI code must not see raw touches: the touch layer turns them into mouse input.
        public static int touchCount => Virtual ? 0 : Input.touchCount;
        public static Touch GetTouch(int i) => Input.GetTouch(i);
        public static Touch[] touches => Virtual ? new Touch[0] : Input.touches;
        public static bool touchSupported => !Virtual && Input.touchSupported;

        public static bool anyKey => Virtual ? anyVirtualKey || mouseCur[0] || mouseCur[1] || mouseCur[2] || Input.anyKey : Input.anyKey;
        public static bool anyKeyDown => Virtual ? anyVirtualKeyDown || GetMouseButtonDown(0) || GetMouseButtonDown(1) || Input.anyKeyDown : Input.anyKeyDown;

        public static float GetAxis(string axis)
        {
            if (!Virtual) return RealAxis(axis, false);
            return Mathf.Clamp(RealAxis(axis, false) + VirtualAxis(axis), axis.StartsWith("Mouse") ? float.MinValue : -1f, axis.StartsWith("Mouse") ? float.MaxValue : 1f);
        }
        public static float GetAxisRaw(string axis)
        {
            if (!Virtual) return RealAxis(axis, true);
            return Mathf.Clamp(RealAxis(axis, true) + VirtualAxis(axis), axis.StartsWith("Mouse") ? float.MinValue : -1f, axis.StartsWith("Mouse") ? float.MaxValue : 1f);
        }
        public static bool GetButton(string b) { try { return Input.GetButton(b); } catch (ArgumentException) { return false; } }
        public static bool GetButtonDown(string b) { try { return Input.GetButtonDown(b); } catch (ArgumentException) { return false; } }
        public static bool GetButtonUp(string b) { try { return Input.GetButtonUp(b); } catch (ArgumentException) { return false; } }

        public static string inputString => Input.inputString;
        public static string compositionString => Input.compositionString;
        public static IMECompositionMode imeCompositionMode { get => Input.imeCompositionMode; set => Input.imeCompositionMode = value; }
        public static Vector2 compositionCursorPos { get => Input.compositionCursorPos; set => Input.compositionCursorPos = value; }
    }
}
