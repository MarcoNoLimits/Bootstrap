using UnityEngine;

/// <summary>
/// Draws the last valid hand ROI rectangle on screen for HoloLens / Editor verification.
/// Assumes the PV image is stretched to the full Game view (or matches <see cref="previewWidth"/> / <see cref="previewHeight"/>).
/// </summary>
public class SignRoiDebugOverlay : MonoBehaviour
{
    [SerializeField] private SignLanguageHandRoiPipeline pipeline;

    // Debug-only overlay. Keep OFF by default on device/runtime to avoid IMGUI overhead/errors.
    [SerializeField] private bool debugDraw = false;

    [SerializeField] private Color borderColor = new Color(0.2f, 1f, 0.35f, 0.95f);

    [Tooltip("If zero, uses Screen.width / Screen.height.")]
    [SerializeField] private float previewWidth;

    [Tooltip("If zero, uses Screen.width / Screen.height.")]
    [SerializeField] private float previewHeight;

    private void OnGUI()
    {
        if (!Application.isEditor)
        {
            return;
        }

        if (!debugDraw || pipeline == null)
        {
            return;
        }

        if (!pipeline.LastHadValidRoi)
        {
            return;
        }

        RectInt roi = pipeline.LastRoi;
        int texW = pipeline.LastPvTextureWidth;
        int texH = pipeline.LastPvTextureHeight;
        if (texW <= 0 || texH <= 0 || roi.width <= 0 || roi.height <= 0)
        {
            return;
        }

        float pw = previewWidth > 0 ? previewWidth : Screen.width;
        float ph = previewHeight > 0 ? previewHeight : Screen.height;
        if (pw <= 1f || ph <= 1f)
        {
            return;
        }

        float sx = pw / texW;
        float sy = ph / texH;
        // ROI uses bottom-left origin (texture space). GUI uses top-left.
        float guiX = roi.x * sx;
        float guiY = ph - (roi.y + roi.height) * sy;
        float w = roi.width * sx;
        float h = roi.height * sy;
        if (!float.IsFinite(guiX) || !float.IsFinite(guiY) || !float.IsFinite(w) || !float.IsFinite(h)
            || w <= 0f || h <= 0f)
        {
            return;
        }

        const float t = 3f;
        Texture2D tex = Texture2D.whiteTexture;
        Color prev = GUI.color;
        try
        {
            GUI.color = borderColor;
            GUI.DrawTexture(new Rect(guiX, guiY, w, t), tex);
            GUI.DrawTexture(new Rect(guiX, guiY + h - t, w, t), tex);
            GUI.DrawTexture(new Rect(guiX, guiY, t, h), tex);
            GUI.DrawTexture(new Rect(guiX + w - t, guiY, t, h), tex);
        }
        finally
        {
            GUI.color = prev;
        }
    }
}
