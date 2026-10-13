using System.Collections;
using UnityEngine;

namespace VigilIOS
{
    // Keeps the game's UI out of the Dynamic Island / rounded corners / home indicator: every edge-anchored
    // panel directly under a screen-space root canvas is pushed inside Screen.safeArea. The 3D world and
    // full-screen overlays (eyelids, fades, backgrounds) still cover the whole screen.
    public static class SafeArea
    {
        sealed class Applied : MonoBehaviour { }

        public static void OnSceneLoaded(MonoBehaviour host)
        {
            if (host != null) host.StartCoroutine(ApplyLater());
        }

        static IEnumerator ApplyLater()
        {
            // UI is built in Awake/Start; catch canvases created a little later too
            yield return null;
            Apply();
            for (int i = 0; i < 3; i++)
            {
                yield return new WaitForSecondsRealtime(1f);
                Apply();
            }
        }

        static bool IsFullScreenOverlay(RectTransform rt)
        {
            string n = rt.name.ToLowerInvariant();
            if (n.Contains("background") || n.Contains("blocker") || n.Contains("fade") || n.Contains("dim") ||
                n.Contains("eye") || n.Contains("lid") || n.Contains("black") || n.Contains("vignette") || n.Contains("overlay"))
                return true;
            // stretched over the whole canvas with no offsets: a full-screen image, not a panel
            return rt.anchorMin == Vector2.zero && rt.anchorMax == Vector2.one && rt.offsetMin == Vector2.zero && rt.offsetMax == Vector2.zero
                   && rt.GetComponent<UnityEngine.UI.Graphic>() != null;
        }

        public static void Apply()
        {
            Rect safe = Screen.safeArea;
            float l = safe.xMin, r = Screen.width - safe.xMax, b = safe.yMin, t = Screen.height - safe.yMax;
            if (l <= 0 && r <= 0 && b <= 0 && t <= 0) return;
            int changed = 0;
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (!canvas.isRootCanvas || canvas.renderMode == RenderMode.WorldSpace) continue;
                if (canvas.GetComponent<Applied>() != null) continue;
                canvas.gameObject.AddComponent<Applied>();
                float s = canvas.scaleFactor > 0 ? canvas.scaleFactor : 1f;
                float L = l / s, R = r / s, B = b / s, T = t / s;
                foreach (Transform child in canvas.transform)
                {
                    var rt = child as RectTransform;
                    if (rt == null || IsFullScreenOverlay(rt)) continue;
                    Vector2 min = rt.offsetMin, max = rt.offsetMax;
                    if (rt.anchorMin.x <= 0.01f && rt.anchorMax.x >= 0.99f) { min.x += L; max.x -= R; }
                    else if (rt.anchorMax.x <= 0.01f) { min.x += L; max.x += L; }
                    else if (rt.anchorMin.x >= 0.99f) { min.x -= R; max.x -= R; }
                    if (rt.anchorMin.y <= 0.01f && rt.anchorMax.y >= 0.99f) { min.y += B; max.y -= T; }
                    else if (rt.anchorMax.y <= 0.01f) { min.y += B; max.y += B; }
                    else if (rt.anchorMin.y >= 0.99f) { min.y -= T; max.y -= T; }
                    if (min != rt.offsetMin || max != rt.offsetMax)
                    {
                        rt.offsetMin = min;
                        rt.offsetMax = max;
                        changed++;
                    }
                }
            }
            if (changed > 0) Log.Info($"safe area: moved {changed} UI panels inside {safe}");
        }
    }
}
