using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VigilShim
{
    /// <summary>
    /// FMOD plugins on iOS. The game's FMODUnity.dll was compiled for Windows, where plugins are DLLs
    /// loaded at runtime (System::loadPlugin); iOS has no dynamic libraries, so the patcher makes
    /// FMODUnity's Platform.LoadDynamicPlugins do nothing and Platform.LoadStaticPlugins call
    /// <see cref="RegisterStaticPlugins"/>, which registers the statically linked Steam Audio FMOD
    /// plugin (libphonon_fmod.a) before any bank is loaded (the banks use its "Steam Audio Spatializer").
    /// </summary>
    public static class FmodPlugins
    {
        [DllImport("__Internal")] static extern int VigilFmod_RegisterStaticPlugins(IntPtr coreSystem);
        [DllImport("__Internal")] static extern IntPtr VigilFmod_LastMessage();

        public static int Registered = -1;
        public static string Message = "";

        public static void RegisterStaticPlugins(IntPtr coreSystem)
        {
            if (Application.platform != RuntimePlatform.IPhonePlayer) return;
            try
            {
                Registered = VigilFmod_RegisterStaticPlugins(coreSystem);
                var p = VigilFmod_LastMessage();
                Message = p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);
                if (Registered > 0) Debug.Log($"[Vigil-iOS] FMOD: registered {Registered} static plugin DSP(s): {Message}");
                else Debug.LogError("[Vigil-iOS] FMOD: no static plugin registered (3D sounds will fail to load): " + Message);
            }
            catch (Exception e)
            {
                Message = e.ToString();
                Debug.LogError("[Vigil-iOS] FMOD static plugin registration failed: " + e);
            }
        }
    }

    /// <summary>MFi / Xbox / PlayStation controllers through Apple's GameController framework.</summary>
    public static class Pad
    {
        [DllImport("__Internal")] static extern int VigilPad_Poll(float[] axes, out int buttons);

        // buttons bitmask (VigilNative.mm)
        public const int A = 1, B = 2, X = 4, Y = 8, LB = 16, RB = 32, LT = 64, RT = 128,
            Menu = 256, Options = 512, L3 = 1024, R3 = 2048, Up = 4096, Down = 8192, Left = 16384, Right = 32768;

        public static bool Connected;
        public static string Name = "";
        /// left x/y, right x/y, left trigger, right trigger
        public static readonly float[] Axes = new float[6];
        public static int Buttons, PrevButtons;

        public static bool Held(int b) => (Buttons & b) != 0;
        public static bool Pressed(int b) => (Buttons & b) != 0 && (PrevButtons & b) == 0;
        public static bool Released(int b) => (Buttons & b) == 0 && (PrevButtons & b) != 0;

        static bool broken;

        public static void Poll()
        {
            PrevButtons = Buttons;
            if (broken || Application.platform != RuntimePlatform.IPhonePlayer) { Connected = false; Buttons = 0; return; }
            try
            {
                Connected = VigilPad_Poll(Axes, out Buttons) != 0;
                if (!Connected) { Buttons = 0; Array.Clear(Axes, 0, Axes.Length); }
            }
            catch (Exception e)
            {
                broken = true;
                Connected = false;
                Debug.LogError("[Vigil-iOS] controller support unavailable: " + e.Message);
            }
        }
    }
}
