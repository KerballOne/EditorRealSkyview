using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class ERSV_Config : MonoBehaviour
{
    // Ambient lighting
    public static float skyMultiplier        = 1.00f;
    public static float equatorMultiplier    = 0.75f;
    public static float groundMultiplier     = 0.40f;
    public static float sunClamp             = 0.85f;
    public static float ambientSaturation    = 0.20f;

    // Scene brightness
    public static float sceneScaleMultiplier = 5.00f;
    public static float roadMultiplier       = 2.00f;
    public static float roadMax              = 0.80f;

    // Shadow caster
    public static float shadowIntensity      = 0.5f;
    public static float shadowAzimuthBias    = 0.0f;
    public static float sphAzimuthBias       = -60.0f;
    public static float vabRoofZenithCutoff  = 0.0f;

    // Building lights
    public static float exteriorRangeThreshold = 100.0f;
    public static float spillRangeFloor        = 0.05f;
    public static float exteriorBoostMax       = 3.00f;
    public static float windowAlbedoMin        = 0.10f;

    // Capture
    public static float captureAltitude      = 420.0f;
    public static float lensFlareMultiplier  = 10.0f;

    // Debug
    public static bool debugLogging          = false;
    public static bool saveFaceImages        = false;

    public static string modRoot             = "";

    void Start()
    {
        modRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Reload();
    }

    public static void Reload()
    {
        string cfgPath = Path.Combine(modRoot, "PluginData", "EditorRealSkyview.cfg");

        if (!File.Exists(cfgPath))
        {
            Debug.LogWarning($"[ERSV] Config not found at {cfgPath}, using defaults");
            return;
        }

        ConfigNode root = ConfigNode.Load(cfgPath);
        ConfigNode node = root?.GetNode("ERSV_CONFIG");
        if (node == null)
        {
            Debug.LogWarning("[ERSV] ERSV_CONFIG node missing from config file, using defaults");
            return;
        }

        Load(node, "skyMultiplier",          ref skyMultiplier);
        Load(node, "equatorMultiplier",       ref equatorMultiplier);
        Load(node, "groundMultiplier",        ref groundMultiplier);
        Load(node, "sunClamp",               ref sunClamp);
        Load(node, "ambientSaturation",       ref ambientSaturation);
        Load(node, "sceneScaleMultiplier",    ref sceneScaleMultiplier);
        Load(node, "roadMultiplier",          ref roadMultiplier);
        Load(node, "roadMax",                 ref roadMax);
        Load(node, "shadowIntensity",         ref shadowIntensity);
        Load(node, "shadowAzimuthBias",       ref shadowAzimuthBias);
        Load(node, "sphAzimuthBias",          ref sphAzimuthBias);
        Load(node, "vabRoofZenithCutoff",     ref vabRoofZenithCutoff);
        Load(node, "exteriorRangeThreshold",  ref exteriorRangeThreshold);
        Load(node, "spillRangeFloor",         ref spillRangeFloor);
        Load(node, "exteriorBoostMax",        ref exteriorBoostMax);
        Load(node, "windowAlbedoMin",         ref windowAlbedoMin);
        Load(node, "captureAltitude",         ref captureAltitude);
        Load(node, "lensFlareMultiplier",     ref lensFlareMultiplier);
        Load(node, "debugLogging",            ref debugLogging);
        Load(node, "saveFaceImages",          ref saveFaceImages);

        Debug.Log("[ERSV] Config reloaded");
    }

    static void Load(ConfigNode node, string key, ref float field)
    {
        if (node.HasValue(key) && float.TryParse(
            node.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            field = v;
    }

    static void Load(ConfigNode node, string key, ref bool field)
    {
        if (node.HasValue(key) && bool.TryParse(node.GetValue(key), out bool v))
            field = v;
    }
}
