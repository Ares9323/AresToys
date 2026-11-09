namespace AresToys.Capture;

public interface ICaptureSource
{
    /// <summary>Capture a virtual-screen region and return it as PNG-encoded bytes.</summary>
    Task<CapturedImage> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken);
}
