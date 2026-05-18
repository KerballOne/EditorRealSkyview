using System.Collections;
using UnityEngine;

[KSPAddon(KSPAddon.Startup.EditorVAB, false)]
public class ERSV_Apply : MonoBehaviour
{
    const float sphYawOffset = 60f; // degrees; positive = CCW looking down

    EditorFacility _lastFacility = EditorFacility.None;

    void Start() => StartCoroutine(WaitAndApply());

    void Update()
    {
        if (ERSV_Store.cubemap == null) return;
        if (EditorDriver.editorFacility == _lastFacility) return;
        _lastFacility = EditorDriver.editorFacility;
        if (ERSV_Settings.Instance?.modEnabled == false) return;
        ApplySkybox();
    }

    IEnumerator WaitAndApply()
    {
        float elapsed = 0f;
        float timeout = 30f;

        while (ERSV_Store.cubemap == null && elapsed < timeout)
        {
            yield return new WaitForSeconds(0.25f);
            elapsed += 0.25f;
        }

        if (ERSV_Store.cubemap == null)
        {
            Debug.LogWarning("[ERSV] Timed out waiting for cubemap — return to Space Center and re-enter the editor to trigger a capture");
            yield break;
        }

        if (ERSV_Settings.Instance?.modEnabled == false) yield break;
        ApplySkybox();
    }

    void ApplySkybox()
    {
        Material mat = new Material(RenderSettings.skybox);
        mat.SetTexture("_FrontTex", CubemapFaceToTexture2D(CubemapFace.PositiveX));
        mat.SetTexture("_BackTex",  CubemapFaceToTexture2D(CubemapFace.NegativeX));
        mat.SetTexture("_RightTex", CubemapFaceToTexture2D(CubemapFace.PositiveZ));
        mat.SetTexture("_LeftTex",  CubemapFaceToTexture2D(CubemapFace.NegativeZ));
        mat.SetTexture("_UpTex",    CubemapFaceToTexture2D(CubemapFace.PositiveY));
        mat.SetTexture("_DownTex",  CubemapFaceToTexture2D(CubemapFace.NegativeY));

        if (mat.HasProperty("_Rotation"))
        {
            float rotation = EditorDriver.editorFacility == EditorFacility.SPH ? sphYawOffset : 0f;
            mat.SetFloat("_Rotation", rotation);
        }

        RenderSettings.skybox = mat;
        DynamicGI.UpdateEnvironment();
    }

    Texture2D CubemapFaceToTexture2D(CubemapFace face)
    {
        Texture2D tex = new Texture2D(ERSV_Store.cubemap.width, ERSV_Store.cubemap.height, TextureFormat.RGB24, false);
        tex.SetPixels(ERSV_Store.cubemap.GetPixels(face));
        // Clamp prevents bilinear filtering from wrapping to the opposite edge at face
        // boundaries, which reads the wrong colour and produces a visible seam at night.
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply();
        return tex;
    }
}
