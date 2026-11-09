using AresToys.Storage.Protection;

namespace AresToys.Storage.Items;

/// <summary>Encrypts/decrypts the <c>items.payload</c> column using <see cref="IPayloadProtector"/>.</summary>
public sealed class ItemSerializer
{
    private readonly IPayloadProtector _protector;

    public ItemSerializer(IPayloadProtector protector)
    {
        _protector = protector;
    }

    public byte[] Encode(ReadOnlyMemory<byte> plaintext) => _protector.Protect(plaintext.Span);

    public byte[] Decode(byte[] ciphertext) => _protector.Unprotect(ciphertext);
}
