using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[KSPAddon(KSPAddon.Startup.EditorVAB, false)]
public class ERSV_Lighting : MonoBehaviour
{
    Color _originalLight;
    Color _originalSky;
    Color _originalEquator;
    Color _originalGround;

    List<(Material mat, Color originalColor)>                   _roadMaterials;
    List<(Material mat, Color origEmission, Color origColor)>   _windowMaterials;
    List<(Light light, float origRange, float origIntensity)>   _spillLights;
    List<(Light light, LightShadows origShadows)>               _shadowLights;
    List<(MeshRenderer mr, ShadowCastingMode origMode)>         _buildingMeshes;
    Light _sunLight;
    Light _shadowCaster;

    Vector3 _surfaceUp;
    Vector3 _surfaceEast;
    Vector3 _surfaceNorth;

    float _origShadowDistance = -1f;
    bool  _prevShadowEnabled = false;
    bool  _hasTarget         = false;
    Color _targetLight;
    Color _targetSky;
    Color _targetEquator;
    Color _targetGround;

    EditorFacility _lastFacility = EditorFacility.None;

    bool  _origFogEnabled;
    Color _origFogColor;
    float _origFogStart;
    float _origFogEnd;
    Color _targetFogColor;

    void Start()
    {
        _originalLight   = RenderSettings.ambientLight;
        _originalSky     = RenderSettings.ambientSkyColor;
        _originalEquator = RenderSettings.ambientEquatorColor;
        _originalGround  = RenderSettings.ambientGroundColor;

        _origFogEnabled = RenderSettings.fog;
        _origFogColor   = RenderSettings.fogColor;
        _origFogStart   = RenderSettings.fogStartDistance;
        _origFogEnd     = RenderSettings.fogEndDistance;

        StartCoroutine(WaitAndCalculate());
    }

    void Update()
    {
        ERSV_Settings s = ERSV_Settings.Instance;

        // Re-index and recalculate when facility changes (VAB ↔ SPH have different scene lights).
        if (ERSV_Store.cubemap != null && EditorDriver.editorFacility != _lastFacility)
        {
            if (ERSV_Config.debugLogging) Debug.Log($"[ERSV] Facility changed {_lastFacility} → {EditorDriver.editorFacility}, re-indexing");
            RestoreLightsAndMaterials();
            _lastFacility = EditorDriver.editorFacility;
            IndexRoadMaterials();
            IndexWindowMaterials();
            IndexBuildingLights();
            IndexBuildingMeshes();
            if (s == null || (s.modEnabled && s.shadowsEnabled))  DisableShadows();
            if (s == null || (s.modEnabled && s.lightingEnabled)) CalculateTargets();
        }

        // Reapply every frame — KSP overwrites RenderSettings continuously.
        if (!_hasTarget) return;
        if (s != null && (!s.modEnabled || !s.lightingEnabled)) return;
        RenderSettings.ambientLight        = _targetLight;
        RenderSettings.ambientSkyColor     = _targetSky;
        RenderSettings.ambientEquatorColor = _targetEquator;
        RenderSettings.ambientGroundColor  = _targetGround;
        RenderSettings.fog      = _origFogEnabled;
        RenderSettings.fogColor = _targetFogColor;
    }

    void LateUpdate()
    {
        ERSV_Settings s = ERSV_Settings.Instance;
        if (s != null && (!s.modEnabled || !s.shadowsEnabled))
        {
            if (_shadowCaster != null) _shadowCaster.enabled = false;
            return;
        }

        if (_sunLight == null || _shadowCaster == null) return;

        // SunLight.forward is in KSP world space and is the light-ray direction (from sun
        // toward Kerbin). Project it into the KSC surface frame computed at startup.
        // Editor axes: X = geographic West (-East), Y = geographic Up, Z = geographic North.
        // Up component < 0 when light goes downward, i.e. sun is above the horizon.
        Vector3 ksp = ERSV_Store.capturedSunForward != Vector3.zero
            ? ERSV_Store.capturedSunForward
            : _sunLight.transform.forward;
        Vector3 corrected = new Vector3(
            -Vector3.Dot(ksp, _surfaceEast),
             Vector3.Dot(ksp, _surfaceUp),
             Vector3.Dot(ksp, _surfaceNorth)).normalized;
        if (EditorDriver.editorFacility == EditorFacility.SPH)
            corrected = Quaternion.AngleAxis(ERSV_Config.sphAzimuthBias, Vector3.up) * corrected;
        if (ERSV_Config.shadowAzimuthBias != 0f)
            corrected = Quaternion.AngleAxis(ERSV_Config.shadowAzimuthBias, Vector3.up) * corrected;

        bool shouldEnable = corrected.y < 0; // corrected.y < 0 = sun above horizon
        if (shouldEnable && EditorDriver.editorFacility == EditorFacility.VAB)
        {
            float elevDeg  = Mathf.Asin(-corrected.y) * Mathf.Rad2Deg;
            bool  afternoon = corrected.x > 0f; // light travels east = sun is in the west
            float arcAngle  = afternoon ? 180f - elevDeg : elevDeg;
            if (Mathf.Abs(arcAngle - 90f) <= ERSV_Config.vabRoofZenithCutoff) shouldEnable = false;
        }

        if (ERSV_Config.debugLogging && shouldEnable != _prevShadowEnabled)
        {
            float elevDeg = Mathf.Asin(-corrected.y) * Mathf.Rad2Deg;
            Debug.Log($"[ERSV] Shadow caster {(shouldEnable ? "enabled" : "disabled")} | UT={Planetarium.GetUniversalTime():F1} | elev={elevDeg:F1}° | dir={corrected}");
        }
        _prevShadowEnabled    = shouldEnable;
        _shadowCaster.enabled = shouldEnable;
        if (_shadowCaster.enabled)
            _shadowCaster.transform.forward = corrected;
    }

    void OnDestroy()
    {
        RenderSettings.ambientLight        = _originalLight;
        RenderSettings.ambientSkyColor     = _originalSky;
        RenderSettings.ambientEquatorColor = _originalEquator;
        RenderSettings.ambientGroundColor  = _originalGround;

        RenderSettings.fog              = _origFogEnabled;
        RenderSettings.fogColor         = _origFogColor;
        RenderSettings.fogStartDistance = _origFogStart;
        RenderSettings.fogEndDistance   = _origFogEnd;

        RestoreLightsAndMaterials();
    }

    IEnumerator WaitAndCalculate()
    {
        float elapsed = 0f;
        while (ERSV_Store.cubemap == null && elapsed < 30f)
        {
            yield return new WaitForSeconds(0.25f);
            elapsed += 0.25f;
        }
        if (ERSV_Store.cubemap == null)
        {
            Debug.LogWarning("[ERSV] Cubemap not available after 30s — lighting will not apply");
            yield break;
        }
        if (ERSV_Config.debugLogging) Debug.Log($"[ERSV] Cubemap ready after {elapsed:F2}s, indexing scene");

        ERSV_Settings s = ERSV_Settings.Instance;
        IndexRoadMaterials();
        IndexWindowMaterials();
        IndexBuildingLights();
        IndexBuildingMeshes();
        if (s == null || (s.modEnabled && s.shadowsEnabled))  DisableShadows();
        _lastFacility = EditorDriver.editorFacility;
        if (s == null || (s.modEnabled && s.lightingEnabled)) CalculateTargets();
    }

    void IndexRoadMaterials()
    {
        string[] keywords = { "asphalt", "concrete", "terrain", "grass", "launchpad", "runway", "ksc_pad", "ksp_pad", "ramp" };
        _roadMaterials = new List<(Material, Color)>();
        var seen = new HashSet<int>();

        foreach (MeshRenderer mr in FindObjectsOfType<MeshRenderer>())
        {
            foreach (Material mat in mr.sharedMaterials)
            {
                if (mat == null) continue;
                if (!mat.HasProperty("_Color")) continue;
                if (!seen.Add(mat.GetInstanceID())) continue;

                string matName = mat.name.Replace(" (Instance)", "").Trim();
                bool isRoad = false;
                foreach (string kw in keywords)
                    if (matName.ToLower().Contains(kw)) { isRoad = true; break; }
                if (!isRoad) continue;

                _roadMaterials.Add((mat, mat.color));
            }
        }
        if (ERSV_Config.debugLogging) Debug.Log($"[ERSV] Indexed {_roadMaterials.Count} road/terrain materials");
    }

    void IndexWindowMaterials()
    {
        string[] keywords = { "window", "glass", "pane" };
        _windowMaterials = new List<(Material, Color, Color)>();
        var seen = new HashSet<int>();

        foreach (MeshRenderer mr in FindObjectsOfType<MeshRenderer>())
        {
            foreach (Material mat in mr.sharedMaterials)
            {
                if (mat == null) continue;
                if (!mat.HasProperty("_EmissionColor")) continue;
                if (!seen.Add(mat.GetInstanceID())) continue;

                string matName = mat.name.Replace(" (Instance)", "").Trim().ToLower();
                bool isWindow = false;
                foreach (string kw in keywords)
                    if (matName.Contains(kw)) { isWindow = true; break; }
                if (!isWindow) continue;

                Color origColor = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
                _windowMaterials.Add((mat, mat.GetColor("_EmissionColor"), origColor));
            }
        }
        if (ERSV_Config.debugLogging) Debug.Log($"[ERSV] Indexed {_windowMaterials.Count} window materials");
    }

    void IndexBuildingMeshes()
    {
        _buildingMeshes = new List<(MeshRenderer, ShadowCastingMode)>();
        foreach (MeshRenderer mr in FindObjectsOfType<MeshRenderer>())
        {
            if (mr.shadowCastingMode != ShadowCastingMode.Off) continue;
            _buildingMeshes.Add((mr, ShadowCastingMode.Off));
            mr.shadowCastingMode = ShadowCastingMode.On;
        }
    }

    void IndexBuildingLights()
    {
        _spillLights = new List<(Light, float, float)>();

        // FindObjectsOfTypeAll captures lights on inactive GameObjects that KSP activates later.
        foreach (Light l in Resources.FindObjectsOfTypeAll<Light>())
        {
            if (l.type == LightType.Directional) continue;
            string n = l.name.ToLower();
            if (n.Contains("part") || n.Contains("cargo")) continue;
            _spillLights.Add((l, l.range, l.intensity));
        }
        if (ERSV_Config.debugLogging) Debug.Log($"[ERSV] Indexed {_spillLights.Count} building lights");
    }

    void RestoreLightsAndMaterials()
    {
        if (_roadMaterials != null)
            foreach (var (mat, orig) in _roadMaterials)
                if (mat != null) mat.color = orig;

        if (_windowMaterials != null)
            foreach (var (mat, origEmission, origColor) in _windowMaterials)
                if (mat != null)
                {
                    mat.SetColor("_EmissionColor", origEmission);
                    if (mat.HasProperty("_Color")) mat.SetColor("_Color", origColor);
                }

        if (_spillLights != null)
            foreach (var (light, origRange, origIntensity) in _spillLights)
                if (light != null)
                {
                    light.range     = origRange;
                    light.intensity = origIntensity;
                }

        if (_shadowLights != null)
            foreach (var (light, origShadows) in _shadowLights)
                if (light != null)
                    light.shadows = origShadows;

        if (_buildingMeshes != null)
            foreach (var (mr, origMode) in _buildingMeshes)
                if (mr != null) mr.shadowCastingMode = origMode;

        if (_shadowCaster != null) { Destroy(_shadowCaster.gameObject); _shadowCaster = null; }
        if (_origShadowDistance >= 0f) { QualitySettings.shadowDistance = _origShadowDistance; _origShadowDistance = -1f; }
        _sunLight = null;
    }

    void DisableShadows()
    {
        _sunLight     = null;
        _shadowLights = new List<(Light, LightShadows)>();
        foreach (Light l in Resources.FindObjectsOfTypeAll<Light>())
        {
            if (l.name == "SunLight") { _sunLight = l; continue; }
            if (l.shadows == LightShadows.None) continue;
            _shadowLights.Add((l, l.shadows));
            l.shadows = LightShadows.None;
        }

        _origShadowDistance = QualitySettings.shadowDistance;
        QualitySettings.shadowDistance = ERSV_Config.shadowDistance;

        // Create a shadow-only directional light we fully control — copying SunLight's shadow
        // settings but leaving SunLight itself untouched as KSP intends.
        if (_shadowCaster != null) Destroy(_shadowCaster.gameObject);
        if (_sunLight != null)
        {
            _shadowCaster = new GameObject("ERSV_ShadowCaster").AddComponent<Light>();
            _shadowCaster.type           = LightType.Directional;
            _shadowCaster.intensity      = _sunLight.intensity * ERSV_Config.shadowIntensity;
            _shadowCaster.shadows        = _sunLight.shadows;
            _shadowCaster.shadowStrength = _sunLight.shadowStrength;
            _shadowCaster.shadowBias             = _sunLight.shadowBias;
            _shadowCaster.shadowCustomResolution = ERSV_Config.shadowResolution;
            _shadowCaster.cullingMask            = -1; // all layers, so roof/walls block sunlight
            if (ERSV_Config.debugLogging) Debug.Log($"[ERSV] Shadow caster created | intensity={_shadowCaster.intensity:F2} | strength={_shadowCaster.shadowStrength:F2} | bias={_shadowCaster.shadowBias:F3} | suppressed {_shadowLights.Count} other shadow lights");
        }
        else
        {
            Debug.LogWarning("[ERSV] SunLight not found — shadow caster will not be created");
        }

        // Compute KSC surface frame in world space. Constant for the editor session.
        CelestialBody home = FlightGlobals.GetHomeBody();
        if (home != null)
        {
            double lat = SpaceCenter.Instance != null ? SpaceCenter.Instance.Latitude  : -0.09694;
            double lon = SpaceCenter.Instance != null ? SpaceCenter.Instance.Longitude : 285.44279;
            _surfaceUp    = (Vector3)home.GetSurfaceNVector(lat, lon);
            Vector3d bodyNorth = home.bodyTransform != null
                ? (Vector3d)(home.bodyTransform.rotation * Vector3d.up)
                : Planetarium.up;
            _surfaceEast  = (Vector3)Vector3d.Cross(bodyNorth, (Vector3d)_surfaceUp).normalized;
            _surfaceNorth = Vector3.Cross(_surfaceUp, _surfaceEast).normalized;
        }
        else
        {
            _surfaceUp    = new Vector3(-0.8228f, -0.0016f,  0.5683f);
            _surfaceEast  = new Vector3( 0.5683f,  0.0000f,  0.8228f);
            _surfaceNorth = Vector3.up;
        }
    }

    void CalculateTargets()
    {
        // PositiveY carries the true atmospheric color. Per-channel clamping prevents the
        // sun disk from blowing out the average. Sqrt gamma lift spreads the dark range so
        // nighttime stars produce a visible tint rather than near-zero ambient.
        Color raw = ClampedFaceAverage(CubemapFace.PositiveY);
        Color env = new Color(Mathf.Sqrt(raw.r), Mathf.Sqrt(raw.g), Mathf.Sqrt(raw.b), 1f);

        // Desaturate toward luminance — prevents night-sky blue tint on interior surfaces.
        float envGray = env.r * 0.299f + env.g * 0.587f + env.b * 0.114f;
        env = new Color(
            Mathf.Lerp(envGray, env.r, ERSV_Config.ambientSaturation),
            Mathf.Lerp(envGray, env.g, ERSV_Config.ambientSaturation),
            Mathf.Lerp(envGray, env.b, ERSV_Config.ambientSaturation),
            1f);

        // Raw (pre-sqrt) luminance — drops more aggressively at night than the gamma-lifted value.
        float rawLum    = raw.r * 0.299f + raw.g * 0.587f + raw.b * 0.114f;
        float sceneScale = Mathf.Clamp01(rawLum * ERSV_Config.sceneScaleMultiplier);

        // Lerp between our sky-derived ambient and KSP's original. ambientT floors at 0.5 so
        // deep night is halfway between our value and KSP's original rather than fully ours.
        float ambientT = Mathf.Lerp(0.5f, 1f, sceneScale);
        _targetLight   = Color.Lerp(new Color(env.r * ERSV_Config.equatorMultiplier, env.g * ERSV_Config.equatorMultiplier, env.b * ERSV_Config.equatorMultiplier, 1f), _originalLight,   ambientT);
        _targetSky     = Color.Lerp(new Color(env.r * ERSV_Config.skyMultiplier,     env.g * ERSV_Config.skyMultiplier,     env.b * ERSV_Config.skyMultiplier,     1f), _originalSky,     ambientT);
        _targetEquator = Color.Lerp(new Color(env.r * ERSV_Config.equatorMultiplier, env.g * ERSV_Config.equatorMultiplier, env.b * ERSV_Config.equatorMultiplier, 1f), _originalEquator, ambientT);
        _targetGround  = Color.Lerp(new Color(env.r * ERSV_Config.groundMultiplier,  env.g * ERSV_Config.groundMultiplier,  env.b * ERSV_Config.groundMultiplier,  1f), _originalGround,  ambientT);
        _hasTarget = true;
        if (ERSV_Config.debugLogging) Debug.Log($"[ERSV] Ambient targets | rawLum={rawLum:F3} sceneScale={sceneScale:F3} ambientT={ambientT:F3} | sky={_targetSky} equator={_targetEquator} ground={_targetGround}");

        float envLum = env.r * 0.299f + env.g * 0.587f + env.b * 0.114f;
        if (_roadMaterials != null)
        {
            float roadScale = Mathf.Lerp(Mathf.Clamp01(envLum * ERSV_Config.roadMultiplier), ERSV_Config.roadMax, sceneScale);
            foreach (var (mat, orig) in _roadMaterials)
                if (mat != null)
                    mat.color = new Color(orig.r * roadScale, orig.g * roadScale, orig.b * roadScale, orig.a);
        }

        _targetFogColor = new Color(
            _origFogColor.r * sceneScale,
            _origFogColor.g * sceneScale,
            _origFogColor.b * sceneScale,
            _origFogColor.a);

        // Window materials: zero emission always; dim albedo toward windowAlbedoMin at night.
        if (_windowMaterials != null)
        {
            float albedoScale = Mathf.Lerp(ERSV_Config.windowAlbedoMin, 1f, sceneScale);
            foreach (var (mat, _, origColor) in _windowMaterials)
                if (mat != null)
                {
                    mat.SetColor("_EmissionColor", Color.black);
                    if (mat.HasProperty("_Color"))
                        mat.SetColor("_Color", origColor * albedoScale);
                }
        }

        if (_spillLights != null)
        {
            float spillScale    = Mathf.Max(sceneScale, ERSV_Config.spillRangeFloor);
            float exteriorBoost = Mathf.Lerp(ERSV_Config.exteriorBoostMax, 1f, sceneScale);
            float windowScale   = Mathf.Lerp(ERSV_Config.windowAlbedoMin, 1f, sceneScale);

            foreach (var (light, origRange, origIntensity) in _spillLights)
            {
                if (light == null) continue;
                if (origRange > ERSV_Config.exteriorRangeThreshold)
                {
                    // Exterior spotlight — scale range down at night, boost intensity to compensate.
                    light.range     = origRange * spillScale;
                    light.intensity = origIntensity * exteriorBoost;
                }
                else if (light.name.ToLower().Contains("window"))
                {
                    // Window light — dim at night, full range.
                    light.range     = origRange;
                    light.intensity = origIntensity * windowScale;
                }
                else
                {
                    // Interior building light — no range or intensity change.
                    light.range     = origRange;
                    light.intensity = origIntensity;
                }
            }
        }
    }

    Color ClampedFaceAverage(CubemapFace face)
    {
        Color[] pixels = ERSV_Store.cubemap.GetPixels(face);
        float r = 0, g = 0, b = 0;
        foreach (Color c in pixels)
        {
            r += Mathf.Min(c.r, ERSV_Config.sunClamp);
            g += Mathf.Min(c.g, ERSV_Config.sunClamp);
            b += Mathf.Min(c.b, ERSV_Config.sunClamp);
        }
        float inv = 1f / pixels.Length;
        return new Color(r * inv, g * inv, b * inv);
    }
}
