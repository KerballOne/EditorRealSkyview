using UnityEngine;

public enum ERSV_CaptureResolution { Quarter, Half, Full }

public class ERSV_Settings : GameParameters.CustomParameterNode
{
    public override string Title          => "Editor Real Skyview";
    public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
    public override string Section        => "Editor Real Skyview";
    public override string DisplaySection => "Editor Real Skyview";
    public override int    SectionOrder   => 1;
    public override bool   HasPresets     => false;

    [GameParameters.CustomParameterUI("Enable Mod",
        toolTip = "Master switch. When off, ERSV makes no changes to the editor scene.")]
    public bool modEnabled = true;

    [GameParameters.CustomParameterUI("Capture Resolution",
        toolTip = "Resolution of the sky cubemap. Lower values use less memory and capture faster.")]
    public ERSV_CaptureResolution captureResolution = ERSV_CaptureResolution.Half;

    [GameParameters.CustomParameterUI("Enable Ambient Lighting",
        toolTip = "Apply sky-matched ambient lighting in the VAB and SPH. Requires Enable Mod.")]
    public bool lightingEnabled = true;

    [GameParameters.CustomParameterUI("Enable Shadows",
        toolTip = "Cast sun-direction shadows in the VAB and SPH. Requires Enable Mod.")]
    public bool shadowsEnabled = true;

    [GameParameters.CustomParameterUI("Reload Config File",
        toolTip = "Check to reload EditorRealSkyview.cfg from disk. The checkbox will clear itself automatically.")]
    public bool reloadConfig = false;

    public override void OnSave(ConfigNode node)
    {
        bool shouldReload = reloadConfig;
        reloadConfig = false;   // reset before base writes fields to node
        base.OnSave(node);
        if (shouldReload) ERSV_Config.Reload();
    }

    public static ERSV_Settings Instance =>
        HighLogic.CurrentGame?.Parameters.CustomParams<ERSV_Settings>();
}
