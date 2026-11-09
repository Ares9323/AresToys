using AresToys.Core.Domain;
using Xunit;

namespace AresToys.Core.Tests.Domain;

public class ItemTests
{
    [Fact]
    public void Item_RoundTripsAllRequiredFields()
    {
        var item = new Item(
            Id: 42,
            Kind: ItemKind.Image,
            Source: ItemSource.CaptureRegion,
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            PayloadSize: 12345);

        Assert.Equal(42, item.Id);
        Assert.Equal(ItemKind.Image, item.Kind);
        Assert.Equal(ItemSource.CaptureRegion, item.Source);
        Assert.Equal(1_700_000_000_000, item.CreatedAt.ToUnixTimeMilliseconds());
        Assert.Equal(12345, item.PayloadSize);
    }

    [Fact]
    public void Item_DefaultsOptionalFieldsToNullOrFalse()
    {
        var item = new Item(
            Id: 1,
            Kind: ItemKind.Text,
            Source: ItemSource.Clipboard,
            CreatedAt: DateTimeOffset.UtcNow,
            PayloadSize: 0);

        Assert.False(item.Pinned);
        Assert.Null(item.DeletedAt);
        Assert.Null(item.SourceProcess);
        Assert.Null(item.SourceWindow);
        Assert.Null(item.BlobRef);
        Assert.Null(item.UploadedUrl);
        Assert.Null(item.UploaderId);
    }

    [Fact]
    public void Item_WithExpression_ProducesNewInstanceLeavingOriginalUnchanged()
    {
        var original = new Item(
            Id: 1,
            Kind: ItemKind.Text,
            Source: ItemSource.Clipboard,
            CreatedAt: DateTimeOffset.UtcNow,
            PayloadSize: 0);

        var pinned = original with { Pinned = true };

        Assert.False(original.Pinned);
        Assert.True(pinned.Pinned);
        Assert.Equal(original.Id, pinned.Id);
    }
}
