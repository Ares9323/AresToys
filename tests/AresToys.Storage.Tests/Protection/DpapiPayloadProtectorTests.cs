using System.Security.Cryptography;
using System.Text;
using AresToys.Storage.Protection;
using Xunit;

namespace AresToys.Storage.Tests.Protection;

public class DpapiPayloadProtectorTests
{
    private readonly IPayloadProtector _protector = new DpapiPayloadProtector();

    [Fact]
    public void Protect_Then_Unprotect_RecoversPlaintext()
    {
        var plaintext = Encoding.UTF8.GetBytes("hello clipboard world");

        var ciphertext = _protector.Protect(plaintext);
        var roundTrip = _protector.Unprotect(ciphertext);

        Assert.Equal(plaintext, roundTrip);
    }

    [Fact]
    public void Protect_ProducesDifferentCiphertextThanPlaintext()
    {
        var plaintext = Encoding.UTF8.GetBytes("not the same");

        var ciphertext = _protector.Protect(plaintext);

        Assert.NotEqual(plaintext, ciphertext);
        Assert.True(ciphertext.Length > plaintext.Length, "DPAPI ciphertext should always be larger than plaintext.");
    }

    [Fact]
    public void Protect_OfEmptyInput_RoundTripsToEmpty()
    {
        var ciphertext = _protector.Protect(ReadOnlySpan<byte>.Empty);

        var roundTrip = _protector.Unprotect(ciphertext);

        Assert.Empty(roundTrip);
    }

    [Fact]
    public void Unprotect_OfTamperedCiphertext_Throws()
    {
        var ciphertext = _protector.Protect(Encoding.UTF8.GetBytes("payload"));
        ciphertext[^1] ^= 0xFF;

        Assert.Throws<CryptographicException>(() => _protector.Unprotect(ciphertext));
    }
}
