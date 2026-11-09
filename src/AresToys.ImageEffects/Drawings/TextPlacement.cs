namespace AresToys.ImageEffects.Drawings;

/// <summary>9-point anchor for text placement on the canvas. Names mirror
/// <c>System.Drawing.ContentAlignment</c> so ShareX <c>.sxie</c> presets round-trip the
/// <c>Placement</c> field unchanged.</summary>
public enum TextPlacement
{
    TopLeft = 1,
    TopCenter = 2,
    TopRight = 4,
    MiddleLeft = 16,
    MiddleCenter = 32,
    MiddleRight = 64,
    BottomLeft = 256,
    BottomCenter = 512,
    BottomRight = 1024,
}
