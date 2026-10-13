using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VigilShim
{
    /// <summary>Stand-ins for desktop-only calls the patcher redirects here.</summary>
    public static class Platform
    {
        // ---- System.Diagnostics.Process: there are no other processes on iOS ------------------
        public static Process ProcessStart(ProcessStartInfo info)
        {
            Debug.LogWarning("[Vigil-iOS] Process.Start blocked: " + (info?.FileName ?? "<null>") + " " + (info?.Arguments ?? ""));
            return null;
        }
        public static Process ProcessStart(string file)
        {
            if (file != null && (file.StartsWith("http://") || file.StartsWith("https://")))
            {
                Application.OpenURL(file);
                return null;
            }
            Debug.LogWarning("[Vigil-iOS] Process.Start blocked: " + file);
            return null;
        }
        public static Process ProcessStart(string file, string args)
        {
            Debug.LogWarning("[Vigil-iOS] Process.Start blocked: " + file + " " + args);
            return null;
        }
        public static bool ProcessStartInstance(Process p)
        {
            Debug.LogWarning("[Vigil-iOS] Process.Start blocked: " + (p?.StartInfo?.FileName ?? "<null>"));
            return false;
        }
        public static Process[] GetProcessesByName(string name) => new Process[0];

        // ---- Application ------------------------------------------------------------------------
        /// Set by the mod: flushes the log before the app goes away.
        public static Action BeforeQuit;

        public static void Quit()
        {
            Debug.Log("[Vigil-iOS] Application.Quit requested by the game");
            try { BeforeQuit?.Invoke(); } catch { }
            Application.Quit();
        }

        // ---- Screen: the iOS player always runs fullscreen at the native resolution; resolution is
        // controlled by the iOS options (render scale), so the game's desktop options do nothing. -------
        public static void SetResolution(int width, int height, bool fullscreen)
        {
            Debug.Log($"[Vigil-iOS] Screen.SetResolution({width}x{height}, {fullscreen}) ignored on iOS (use the iOS render scale option)");
        }
        public static void SetFullScreen(bool fullscreen) { }
        public static bool GetFullScreen() => true;
    }

    /// <summary>Cursor state as the game sets it. iOS has no cursor; the touch layer reads this to tell
    /// gameplay (locked: stick + look + buttons) from menus (unlocked: touch acts as the mouse).</summary>
    public static class VCursor
    {
        public static CursorLockMode LockState = CursorLockMode.None;
        public static bool Visible = true;
        /// Frame (VInput.Frame) of the last change, so the touch layer can react once.
        public static int ChangedFrame;

        public static void set_lockState(CursorLockMode m)
        {
            if (m != LockState) ChangedFrame = VInput.Frame;
            LockState = m;
        }
        public static CursorLockMode get_lockState() => LockState;
        public static void set_visible(bool v)
        {
            if (v != Visible) ChangedFrame = VInput.Frame;
            Visible = v;
        }
        public static bool get_visible() => Visible;
    }

    /// <summary>Where things live on iOS. Everything the user can see or edit is under Documents
    /// (visible in Files / Filza): the log, the settings file and the game content folder.</summary>
    public static class Paths
    {
        static string documents;
        public static string Documents
        {
            get
            {
                if (documents == null)
                {
                    documents = Application.persistentDataPath;
                    try { Directory.CreateDirectory(documents); } catch { }
                }
                return documents;
            }
        }

        /// Folder the user copies the converted game data into (Documents/VigilData).
        public const string ContentFolderName = "VigilData";
        public static string Content => Path.Combine(Documents, ContentFolderName);

        /// Replacement for Application.dataPath in the game assemblies.
        public static string DataPath => Content;
    }
}
