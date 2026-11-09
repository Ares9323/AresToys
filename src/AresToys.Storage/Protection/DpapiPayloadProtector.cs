using System.Security.Cryptography;
using System.Text;

namespace AresToys.Storage.Protection;

public sealed class DpapiPayloadProtector : IPayloadProtector
{
    // Fixed application-bound entropy: not a secret, but ensures AresToys ciphertexts cannot be unprotected
    // by an unrelated DPAPI consumer running in the same user session.
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("AresToys.Storage.v1");

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
        => ProtectedData.Protect(plaintext.ToArray(), AppEntropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        => ProtectedData.Unprotect(ciphertext.ToArray(), AppEntropy, DataProtectionScope.CurrentUser);
}
