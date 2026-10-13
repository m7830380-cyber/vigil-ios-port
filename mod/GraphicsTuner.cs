using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VigilIOS
{
    // Applies the iOS graphics options on top of the game's URP settings:
    //  * render scale (URP asset renderScale; the UI stays at full resolution)
    //  * shadows: Off / Low / Medium / High (= the game's own settings)
    //  * volumetric fog quality: ray-march steps and blur passes (Low / Medium / High = the game's own)
    //  * TAA and motion blur off by default (cameras using TAA get FXAA instead)
    //  * frame rate cap (30 / 60)
    // The game's original values are remembered per asset / profile / camera, so "High" restores them.
    public static class GraphicsTuner
    {
        sealed class UrpOriginal
        {
            public float RenderScale, ShadowDistance;
            public int Cascades, MainRes, AddRes;
            public bool MainShadows, AddShadows, Soft;
        }

        static readonly Dictionary<int, UrpOriginal> s_Urp = new Dictionary<int, UrpOriginal>();
        static readonly Dictionary<int, (int steps, int blur)> s_Fog = new Dictionary<int, (int, int)>();
        static readonly Dictionary<int, bool> s_MotionBlur = new Dictionary<int, bool>();
        static readonly Dictionary<int, AntialiasingMode> s_CamAA = new Dictionary<int, AntialiasingMode>();
        static float s_NextCameraScan;
        static bool s_LoggedUrp;

        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static T Get<T>(object o, string field)
        {
            var f = o.GetType().GetField(field, F);
            if (f == null) throw new MissingFieldException(o.GetType().Name, field);
            object v = f.GetValue(o);
            if (typeof(T) == typeof(int) && v != null && v.GetType().IsEnum) return (T)(object)Convert.ToInt32(v);
            return (T)v;
        }

        static void Set(object o, string field, object value)
        {
            var f = o.GetType().GetField(field, F);
            if (f == null) throw new MissingFieldException(o.GetType().Name, field);
            if (f.FieldType.IsEnum) value = Enum.ToObject(f.FieldType, value);
            f.SetValue(o, value);
        }

        /// Everything (call after option changes and scene loads).
        public static void Apply()
        {
            Application.targetFrameRate = Options.TargetFps;
            try { ApplyQualityLevel(); } catch (Exception e) { Log.Error("graphics (quality level): " + e); }
            QualitySettings.vSyncCount = 0;
            try { ApplyUrp(); } catch (Exception e) { Log.Error("graphics (URP asset): " + e); }
            try { ApplyVolumes(); } catch (Exception e) { Log.Error("graphics (volumes): " + e); }
            try { ApplyCameras(true); } catch (Exception e) { Log.Error("graphics (cameras): " + e); }
        }

        /// Cheap per-frame part: cameras created later (jumpscare / forklift cameras).
        public static void Tick()
        {
            if (Time.unscaledTime < s_NextCameraScan) return;
            s_NextCameraScan = Time.unscaledTime + 1f;
            try { ApplyCameras(false); } catch (Exception e) { Log.Error("graphics (cameras): " + e); }
        }

        /// The game's quality levels only differ in LOD detail / texture limits here (all use the same URP asset).
        static void ApplyQualityLevel()
        {
            var names = QualitySettings.names;
            int want = Array.IndexOf(names, Options.QualityName);
            if (want < 0) want = Array.IndexOf(names, "High");
            if (want < 0) return;
            if (QualitySettings.GetQualityLevel() != want)
            {
                Log.Info($"quality level {names[QualitySettings.GetQualityLevel()]} -> {names[want]}");
                QualitySettings.SetQualityLevel(want, true);
            }
        }

        public static string[] QualityNames => QualitySettings.names;

        static void ApplyUrp()
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) { Log.Warn("no URP asset active (" + (GraphicsSettings.currentRenderPipeline?.GetType().Name ?? "built-in") + ")"); return; }
            int id = asset.GetInstanceID();
            if (!s_Urp.TryGetValue(id, out var o))
            {
                o = new UrpOriginal
                {
                    RenderScale = asset.renderScale,
                    ShadowDistance = asset.shadowDistance,
                    Cascades = asset.shadowCascadeCount,
                    MainShadows = Get<bool>(asset, "m_MainLightShadowsSupported"),
                    MainRes = Get<int>(asset, "m_MainLightShadowmapResolution"),
                    AddShadows = Get<bool>(asset, "m_AdditionalLightShadowsSupported"),
                    AddRes = Get<int>(asset, "m_AdditionalLightsShadowmapResolution"),
                    Soft = Get<bool>(asset, "m_SoftShadowsSupported"),
                };
                s_Urp[id] = o;
                Log.Info($"URP asset '{asset.name}': renderScale={o.RenderScale} shadows main={o.MainShadows}@{o.MainRes} add={o.AddShadows}@{o.AddRes} " +
                         $"soft={o.Soft} distance={o.ShadowDistance} cascades={o.Cascades} hdr={asset.supportsHDR} msaa={asset.msaaSampleCount}");
            }

            asset.renderScale = Mathf.Clamp(Options.RenderScalePct / 100f, 0.5f, 1f) * Mathf.Min(1f, o.RenderScale <= 0 ? 1f : o.RenderScale);
            switch (Options.Shadows)
            {
                case 0:
                    Set(asset, "m_MainLightShadowsSupported", false);
                    Set(asset, "m_AdditionalLightShadowsSupported", false);
                    break;
                case 1:
                    Set(asset, "m_MainLightShadowsSupported", o.MainShadows);
                    Set(asset, "m_MainLightShadowmapResolution", Math.Min(o.MainRes, 1024));
                    Set(asset, "m_AdditionalLightShadowsSupported", o.AddShadows);
                    Set(asset, "m_AdditionalLightsShadowmapResolution", Math.Min(o.AddRes, 1024));
                    Set(asset, "m_SoftShadowsSupported", false);
                    asset.shadowDistance = Mathf.Min(o.ShadowDistance, 25f);
                    asset.shadowCascadeCount = 1;
                    break;
                case 2:
                    Set(asset, "m_MainLightShadowsSupported", o.MainShadows);
                    Set(asset, "m_MainLightShadowmapResolution", Math.Min(o.MainRes, 2048));
                    Set(asset, "m_AdditionalLightShadowsSupported", o.AddShadows);
                    Set(asset, "m_AdditionalLightsShadowmapResolution", Math.Min(o.AddRes, 2048));
                    Set(asset, "m_SoftShadowsSupported", o.Soft);
                    asset.shadowDistance = Mathf.Min(o.ShadowDistance, 50f);
                    asset.shadowCascadeCount = Math.Min(o.Cascades, 2);
                    break;
                default:
                    Set(asset, "m_MainLightShadowsSupported", o.MainShadows);
                    Set(asset, "m_MainLightShadowmapResolution", o.MainRes);
                    Set(asset, "m_AdditionalLightShadowsSupported", o.AddShadows);
                    Set(asset, "m_AdditionalLightsShadowmapResolution", o.AddRes);
                    Set(asset, "m_SoftShadowsSupported", o.Soft);
                    asset.shadowDistance = o.ShadowDistance;
                    asset.shadowCascadeCount = o.Cascades;
                    break;
            }
            if (!s_LoggedUrp) { s_LoggedUrp = true; Log.Info("graphics applied: " + Options.Describe()); }
        }

        static void ApplyVolumes()
        {
            int fogs = 0, blurs = 0;
            foreach (var profile in Resources.FindObjectsOfTypeAll<VolumeProfile>())
            {
                if (profile == null) continue;
                foreach (var c in profile.components)
                {
                    if (c == null) continue;
                    int id = c.GetInstanceID();
                    if (c is VolumetricFogVolumeComponent fog)
                    {
                        if (!s_Fog.TryGetValue(id, out var orig))
                        {
                            orig = (fog.maxSteps.value, fog.blurIterations.value);
                            s_Fog[id] = orig;
                        }
                        int steps = Options.Fog == 0 ? Math.Min(orig.steps, 32) : Options.Fog == 1 ? Math.Min(orig.steps, 64) : orig.steps;
                        int blur = Options.Fog == 0 ? 1 : Options.Fog == 1 ? Math.Min(orig.blur, 2) : orig.blur;
                        fog.maxSteps.Override(steps);
                        fog.blurIterations.Override(blur);
                        fogs++;
                    }
                    else if (c is MotionBlur mb)
                    {
                        if (!s_MotionBlur.TryGetValue(id, out bool wasActive)) { wasActive = mb.active; s_MotionBlur[id] = wasActive; }
                        mb.active = Options.TaaAndMotionBlur && wasActive;
                        blurs++;
                    }
                }
            }
            Log.Info($"volumes: {fogs} volumetric fog component(s) -> quality {Options.Fog}, {blurs} motion blur component(s) -> {(Options.TaaAndMotionBlur ? "game default" : "off")}");
        }

        static readonly List<Camera> s_Cams = new List<Camera>();

        static void ApplyCameras(bool force)
        {
            int n = Camera.allCamerasCount;
            var arr = new Camera[n];
            Camera.GetAllCameras(arr);
            foreach (var cam in arr)
            {
                if (cam == null) continue;
                int id = cam.GetInstanceID();
                if (!force && s_CamAA.ContainsKey(id)) continue;
                var data = cam.GetComponent<UniversalAdditionalCameraData>();
                if (data == null) continue;
                if (!s_CamAA.TryGetValue(id, out var orig)) { orig = data.antialiasing; s_CamAA[id] = orig; }
                var want = orig == AntialiasingMode.TemporalAntiAliasing && !Options.TaaAndMotionBlur ? AntialiasingMode.FastApproximateAntialiasing : orig;
                if (data.antialiasing != want)
                {
                    data.antialiasing = want;
                    Log.Info($"camera '{cam.name}': antialiasing {orig} -> {want}");
                }
            }
        }
    }
}
