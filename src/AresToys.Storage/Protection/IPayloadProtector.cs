namespace AresToys.Storage.Protection;

public interface IPayloadProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>. Output is opaque ciphertext.</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>Decrypts <paramref name="ciphertext"/> previously produced by <see cref="Protect"/>.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Thrown when ciphertext is corrupt, was produced by another user, or was tampered with.
    /// </exception>
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}
