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
        int size = ERSV_Store.cubemap.width;
        Color[] src = ERSV_Store.cubemap.GetPixels(face);
        Color[] dst = (Color[])src.Clone();

        // The outermost texel row/column of each 90° perspective capture can be dark
        // because the atmosphere geometry doesn't fill the extreme frustum edge.
        // Extend the valid inner content outward so UV=1.0 clamps to a correct colour.
        int n = ERSV_Config.edgeFixPixels;
        for (int d = 0; d < n && n < size / 2; d++)
        {
            for (int i = 0; i < size; i++)
            {
                dst[d * size + i]              = src[n * size + i];
                dst[(size - 1 - d) * size + i] = src[(size - 1 - n) * size + i];
                dst[i * size + d]              = src[i * size + n];
                dst[i * size + (size - 1 - d)] = src[i * size + (size - 1 - n)];
            }
        }

        Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, false);
        tex.SetPixels(dst);
        // Clamp prevents bilinear filtering from wrapping to the opposite edge at face
        // boundaries, which reads the wrong colour and produces a visible seam at night.
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply();
        return tex;
    }
}
