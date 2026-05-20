using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

[KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
public class ERSV_Capture : MonoBehaviour
{

    void Start()
    {
        GameEvents.onGameSceneLoadRequested.Add(OnSceneChangeRequested);
    }

    void OnDestroy()
    {
        GameEvents.onGameSceneLoadRequested.Remove(OnSceneChangeRequested);
    }

    void OnSceneChangeRequested(GameScenes scene)
    {
        if (scene == GameScenes.EDITOR)
            Capture();
    }

    void Capture()
    {
        ERSV_Settings s = ERSV_Settings.Instance;
        if (s != null && !s.modEnabled) return;

        ERSV_Config.Reload();
        if (ERSV_Config.debugLogging) Debug.Log("[ERSV] Capture entered");

        ERSV_CaptureResolution res = s != null ? s.captureResolution : ERSV_CaptureResolution.Half;
        int size;
        switch (res)
        {
            case ERSV_CaptureResolution.Full:    size = Screen.width;     break;
            case ERSV_CaptureResolution.Quarter: size = Screen.width / 4; break;
            default:                             size = Screen.width / 2; break;
        }
        Vector3 vabPos = GetCapturePosition();
        Cubemap cubemap = new Cubemap(size, TextureFormat.RGB24, false);

        Quaternion worldBase = GetWorldAlignedRotation(vabPos);
        if (ERSV_Config.debugLogging) Debug.Log($"[ERSV] worldBase eulerAngles: {worldBase.eulerAngles}");

        Camera[] allCams = Resources.FindObjectsOfTypeAll<Camera>();

        // The SpaceCentre scene composites four cameras, lowest depth first:
        //   GalaxyCamera      (-4) — star field
        //   Camera ScaledSpace(-3) — sun, Mun, planets and the atmosphere shell (blue sky)
        //   Camera 01         (-1) — far half of the local scene
        //   Camera 00         ( 0) — near half of the local scene
        // Only GalaxyCamera does a colour clear; the rest clear depth only, so the
        // colour buffer accumulates each layer on top of the previous one.
        string[] layerNames = { "GalaxyCamera", "Camera ScaledSpace", "Camera 01", "Camera 00" };
        Camera[] layers = layerNames
            .Select(n => allCams.FirstOrDefault(c => c.name == n && c.enabled))
            .Where(c => c != null)
            .ToArray();

        if (layers.Length == 0)
        {
            Debug.LogError("[ERSV] No sky cameras found — aborting capture");
            return;
        }
        if (ERSV_Config.debugLogging)
        {
            Debug.Log("[ERSV] Capture layers: " + string.Join(", ", layers.Select(c => c.name)));
            foreach (Camera layer in layers)
                Debug.Log($"[ERSV]   {layer.name} cullingMask={layer.cullingMask} ({DecodeCullingMask(layer.cullingMask)})");
        }

        // GalaxyCamera and Camera ScaledSpace render in their own coordinate spaces.
        // Moving them puts the camera outside the galaxy / atmosphere shell and the
        // sky goes black — only the local-scene cameras may be lifted above the VAB.
        var moveToVab = new HashSet<string> { "Camera 01", "Camera 00" };

        var faceDirections = new[]
        {
            (face: CubemapFace.PositiveX, fwd: Vector3.right,   up: Vector3.up),
            (face: CubemapFace.NegativeX, fwd: Vector3.left,    up: Vector3.up),
            (face: CubemapFace.PositiveY, fwd: Vector3.up,      up: Vector3.left),
            (face: CubemapFace.NegativeY, fwd: Vector3.down,    up: Vector3.right),
            (face: CubemapFace.PositiveZ, fwd: Vector3.forward, up: Vector3.up),
            (face: CubemapFace.NegativeZ, fwd: Vector3.back,    up: Vector3.up),
        };

        string saveDir = null;
        List<(string path, byte[] data)> pendingWrites = null;
        if (ERSV_Config.saveFaceImages)
        {
            saveDir = System.IO.Path.Combine(ERSV_Config.modRoot, "SkyboxDebug");
            System.IO.Directory.CreateDirectory(saveDir);
            pendingWrites = new List<(string path, byte[] data)>();
        }

        // 24-bit depth so each camera can depth-test within its own near/far range.
        RenderTexture rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
        rt.Create();

        // Strip Camera 00's layer 15 before the warm-up so Scatterer primes in the
        // same culling-mask state as the actual face capture.  If stripped after the
        // warm-up, the changed camera state causes Scatterer to re-trigger its lazy
        // attachment and the first captured face (PositiveX) comes out darker.
        Camera cam00 = layers.FirstOrDefault(c => c.name == "Camera 00");
        int saved00Mask = cam00 != null ? cam00.cullingMask : -1;
        if (cam00 != null && ERSV_Config.stripCam00LocalScenery)
        {
            cam00.cullingMask &= ~(1 << 15);
            if (ERSV_Config.debugLogging)
                Debug.Log($"[ERSV] Camera 00 Local Scenery(15) stripped before warm-up: cullingMask={cam00.cullingMask} ({DecodeCullingMask(cam00.cullingMask)})");
        }

        // Warm-up pass: Scatterer lazily attaches ScatteringCommandBuffer and
        // SkySphereLocalCommandBuffer to Camera 01 via the static Camera.onPreRender
        // event. AddComponent during that callback takes effect on the next Render()
        // call, so without this warm-up the first captured face (PositiveX) runs with
        // no atmospheric scattering on Camera 01 and comes out darker than all others.
        if (ERSV_Config.debugLogging) Debug.Log("[ERSV] Warm-up pass to prime Scatterer/EVE components on Camera 01");
        foreach (Camera layer in layers)
        {
            RenderTexture    wSavedTarget = layer.targetTexture;
            Vector3          wSavedPos    = layer.transform.position;
            Quaternion       wSavedRot    = layer.transform.rotation;
            float            wSavedFov    = layer.fieldOfView;
            float            wSavedAspect = layer.aspect;
            CameraClearFlags wSavedFlags  = layer.clearFlags;

            layer.targetTexture = rt;
            if (moveToVab.Contains(layer.name))
                layer.transform.position = vabPos;
            layer.transform.rotation = worldBase * Quaternion.LookRotation(Vector3.forward, Vector3.up);
            layer.fieldOfView  = 90f;
            layer.aspect       = 1f;
            layer.clearFlags   = CameraClearFlags.Depth;
            layer.Render();

            layer.targetTexture      = wSavedTarget;
            layer.transform.position = wSavedPos;
            layer.transform.rotation = wSavedRot;
            layer.fieldOfView        = wSavedFov;
            layer.aspect             = wSavedAspect;
            layer.clearFlags         = wSavedFlags;
        }
        // Clear any warm-up pixels so they don't bleed into the first real face.
        RenderTexture.active = rt;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = null;

        // LensFlare occlusion is driven by a GPU async depth query whose result is
        // only applied between real Unity frames, so all of our Render() calls share
        // the same occlusion factor from the player's last real frame.  If the player
        // was looking away from the sun that factor can be exactly 0, killing the bloom.
        // We detect the player's view direction here — Camera 00 still holds its Space
        // Centre orientation at this point — and only boost when they were looking away.
        Light sunLight = Resources.FindObjectsOfTypeAll<Light>()
            .FirstOrDefault(l => l.name == "SunLight");
        Camera playerCam = layers.FirstOrDefault(c => c.name == "Camera 00");
        bool needsFlareBoost = false;
        if (playerCam != null && sunLight != null)
        {
            float sunAngle = Vector3.Angle(playerCam.transform.forward, -sunLight.transform.forward);
            // Only boost in the 60°–120° band: sun was off-screen but not so far behind
            // the camera that the LensFlare's stale occlusion factor is unpredictable.
            // Below 60° the player was facing the sun (factor ~1, no boost needed).
            // Above 120° the sun is well behind the camera; boosting amplifies any
            // residual stale occlusion value and produces an oversized bloom.
            needsFlareBoost = sunAngle >= 60f && sunAngle <= 120f;
            if (ERSV_Config.debugLogging)
                Debug.Log($"[ERSV] Player→sun angle={sunAngle:F1}° needsFlareBoost={needsFlareBoost}");
        }

        LensFlare[] sceneFlares = Resources.FindObjectsOfTypeAll<LensFlare>();
        float[] savedFlare = sceneFlares.Select(f => f.brightness).ToArray();
        float flareBoost = needsFlareBoost ? ERSV_Config.lensFlareMultiplier : 1f;
        if (ERSV_Config.debugLogging)
            Debug.Log($"[ERSV] LensFlares ({sceneFlares.Length}) flareBoost={flareBoost:F0}: " +
                string.Join(", ", sceneFlares.Select(f =>
                    $"{f.gameObject.name}(b={f.brightness:F3},spd={f.fadeSpeed:F1},en={f.enabled})")));
        foreach (var f in sceneFlares)
            f.brightness *= flareBoost;

        // Post-processing mods (e.g. CinematicShaders GTAO) attach CommandBuffers to
        // Camera 00 (Camera.main) that read the previous real frame's scene colour in
        // composite mode.  When we call Camera 00.Render() with our off-screen RT those
        // buffers fire and blit the player's Space Centre view onto every cubemap face.
        // Strip Camera 00's buffers for the duration of the capture, then restore them.
        // Camera 01 is left alone so Scatterer's atmosphere buffers keep working.
        var saved00 = new Dictionary<CameraEvent, CommandBuffer[]>();
        if (cam00 != null)
        {
            foreach (CameraEvent evt in (CameraEvent[])System.Enum.GetValues(typeof(CameraEvent)))
            {
                CommandBuffer[] bufs = cam00.GetCommandBuffers(evt);
                if (bufs.Length > 0)
                {
                    saved00[evt] = bufs;
                    cam00.RemoveCommandBuffers(evt);
                }
            }
            if (ERSV_Config.debugLogging)
                Debug.Log($"[ERSV] Stripped {saved00.Count} CommandBuffer event(s) from Camera 00 for capture");
        }

        MeshRenderer[] facilityRenderers = null;
        if (ERSV_Config.stripFacilityRenderers)
        {
            try
            {
                var list = new List<MeshRenderer>();
                string[] targets = { "VehicleAssemblyBuilding", "SpacePlaneHangar" };
                foreach (var facility in PSystemSetup.Instance.SpaceCenterFacilities)
                    foreach (string target in targets)
                        if (facility.facilityName.Contains(target) && facility.facilityTransform != null)
                            list.AddRange(facility.facilityTransform.GetComponentsInChildren<MeshRenderer>());
                facilityRenderers = list.ToArray();
                foreach (var r in facilityRenderers) if (r != null) r.enabled = false;
                if (ERSV_Config.debugLogging)
                {
                    Debug.Log($"[ERSV] Disabled {facilityRenderers.Length} facility renderers for capture");
                    foreach (var r in facilityRenderers)
                        if (r != null) Debug.Log($"[ERSV]   renderer: {r.gameObject.name}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ERSV] stripFacilityRenderers failed, skipping: {e.Message}");
                facilityRenderers = null;
            }
        }

        foreach (var (face, fwd, up) in faceDirections)
        {
            Quaternion rot = worldBase * Quaternion.LookRotation(fwd, up);

            if (ERSV_Config.debugLogging)
                Debug.Log($"[ERSV] === Face {face} ===");

            // Every layer clears depth only, so nothing wipes the colour buffer.
            // Clear it to black ourselves or stale pixels from the previous face
            // bleed through wherever a layer draws nothing.
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = null;

            foreach (Camera layer in layers)
            {
                // Save state
                RenderTexture    savedTarget  = layer.targetTexture;
                Vector3          savedPos     = layer.transform.position;
                Quaternion       savedRot     = layer.transform.rotation;
                float            savedFov     = layer.fieldOfView;
                float            savedAspect  = layer.aspect;
                CameraClearFlags savedFlags   = layer.clearFlags;

                // Configure for this face
                layer.targetTexture = rt;
                if (moveToVab.Contains(layer.name))
                    layer.transform.position = vabPos;
                layer.transform.rotation = rot;
                layer.fieldOfView = 90f;
                layer.aspect = 1f;
                // Depth-only clear: keep what the previous layer drew, reset depth so
                // this camera's own near/far range is valid against a fresh buffer.
                layer.clearFlags = CameraClearFlags.Depth;

                layer.Render();

                // Restore state immediately — no frame gap for the game controller
                // to re-override the transform.
                layer.targetTexture      = savedTarget;
                layer.transform.position = savedPos;
                layer.transform.rotation = savedRot;
                layer.fieldOfView        = savedFov;
                layer.aspect             = savedAspect;
                layer.clearFlags         = savedFlags;
            }

            // Read composited result out of the RT
            RenderTexture.active = rt;
            Texture2D face2D = new Texture2D(size, size, TextureFormat.RGB24, false);
            face2D.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            face2D.Apply();
            RenderTexture.active = null;

            Color[] facePixels = face2D.GetPixels();
            cubemap.SetPixels(facePixels, face);

            if (ERSV_Config.saveFaceImages)
            {
                string path = System.IO.Path.Combine(saveDir, $"{face}.png");
                pendingWrites.Add((path, face2D.EncodeToPNG()));
            }

            Destroy(face2D);
        }

        rt.Release();
        Destroy(rt);

        if (cam00 != null && ERSV_Config.stripCam00LocalScenery)
            cam00.cullingMask = saved00Mask;

        foreach (var kvp in saved00)
            foreach (CommandBuffer buf in kvp.Value)
                cam00.AddCommandBuffer(kvp.Key, buf);

        if (facilityRenderers != null)
            foreach (var r in facilityRenderers) if (r != null) r.enabled = true;

        for (int i = 0; i < sceneFlares.Length; i++)
            sceneFlares[i].brightness = savedFlare[i];

        cubemap.Apply();
        cubemap.SmoothEdges(ERSV_Config.smoothEdgesIterations);
        DontDestroyOnLoad(cubemap);
        ERSV_Store.cubemap = cubemap;

        if (sunLight != null)
            ERSV_Store.capturedSunForward = sunLight.transform.forward;
        if (ERSV_Config.debugLogging) Debug.Log("[ERSV] Capture complete — cubemap stored");

        if (ERSV_Config.saveFaceImages)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var (path, data) in pendingWrites)
                    System.IO.File.WriteAllBytes(path, data);
            });
        }
    }

    static string DecodeCullingMask(int mask)
    {
        if (mask == -1) return "Everything";
        if (mask == 0)  return "Nothing";
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0)
            {
                string n = LayerMask.LayerToName(i);
                names.Add(string.IsNullOrEmpty(n) ? $"Layer{i}" : $"{n}({i})");
            }
        return string.Join(", ", names);
    }

    Quaternion GetWorldAlignedRotation(Vector3 vabPos)
    {
        CelestialBody body = FlightGlobals.Bodies?.FirstOrDefault(b => b.name == "Kerbin")
                             ?? FlightGlobals.currentMainBody;

        Vector3 worldUp = body != null
            ? (vabPos - (Vector3)body.position).normalized
            : Vector3.up;

        Vector3d bodyNorthD  = body?.bodyTransform != null
            ? (Vector3d)(body.bodyTransform.rotation * Vector3d.up)
            : Planetarium.up;
        Vector3 worldForward = (Vector3)Vector3d.Cross(bodyNorthD, (Vector3d)worldUp).normalized;
        if (worldForward.sqrMagnitude < 0.0001f)
            worldForward = Vector3.ProjectOnPlane(Vector3.right, worldUp).normalized;

        if (ERSV_Config.debugLogging) Debug.Log($"[ERSV] worldUp:{worldUp} worldForward:{worldForward}");
        return Quaternion.LookRotation(worldForward, worldUp);
    }

    Vector3 GetCapturePosition()
    {
        string[] candidates = { "VehicleAssemblyBuilding", "SpacePlaneHangar" };
        foreach (string name in candidates)
            foreach (var facility in PSystemSetup.Instance.SpaceCenterFacilities)
                if (facility.facilityName.Contains(name))
                {
                    Vector3 pos = facility.facilityTransform.position;
                    pos += facility.facilityTransform.up * ERSV_Config.captureAltitude;
                    return pos;
                }

        if (ERSV_Config.debugLogging) Debug.LogWarning("[ERSV] No suitable facility found for capture position");
        return Vector3.zero;
    }
}
