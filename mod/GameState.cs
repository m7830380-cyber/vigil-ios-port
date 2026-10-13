using UnityEngine;
using VigilShim;

namespace VigilIOS
{
    // What the player is doing right now, as far as the touch controls care.
    public static class GameState
    {
        public enum Context { Menu, OnFoot, Forklift, Minigame, Cutscene }

        public static Context Current { get; private set; } = Context.Menu;
        static PlayerMovement s_Movement;
        static PlayerSingleton s_MovementOwner;

        public static void Update()
        {
            Current = Compute();
        }

        static Context Compute()
        {
            // the game unlocks the cursor for every menu (main menu, pause, options, calibration)
            if (VCursor.LockState != CursorLockMode.Locked || PlayerPause.isPaused) return Context.Menu;
            if (EnterMinigame.inMiniGame) return Context.Minigame;
            if (ForkliftEnter.onForklift) return Context.Forklift;
            var p = PlayerSingleton.Player;
            if (p == null || !p.gameObject.activeInHierarchy) return Context.Cutscene;
            if (p.playerCamera == null || !p.playerCamera.gameObject.activeInHierarchy) return Context.Cutscene;   // death screen / jumpscare
            if (s_MovementOwner != p || s_Movement == null)
            {
                s_MovementOwner = p;
                var t = p.transform.Find("Player");
                s_Movement = t != null ? t.GetComponent<PlayerMovement>() : null;
            }
            if (s_Movement == null || !s_Movement.isActiveAndEnabled) return Context.Cutscene;   // intro / wake-up
            return Context.OnFoot;
        }

        /// The game reads the mouse position for its crosshair ray (locked cursor = screen centre on PC).
        public static bool CursorLocked => VCursor.LockState == CursorLockMode.Locked;
    }
}
