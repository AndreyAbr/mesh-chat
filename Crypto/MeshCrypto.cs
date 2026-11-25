using System.Security.Cryptography;
using System.Text;

namespace MeshChat.Crypto;

public sealed class MeshCrypto
{
    public const int NonceSize = 12;
    public const int TagSize = 16;

    private readonly byte[] _key;

    public MeshCrypto(string sharedSecret)
    {
        if (string.IsNullOrEmpty(sharedSecret))
            throw new ArgumentException("Shared secret cannot be empty.", nameof(sharedSecret));

        _key = SHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret));
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        var tag = output.AsSpan(NonceSize, TagSize);
        var ciphertext = output.AsSpan(NonceSize + TagSize, plaintext.Length);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> encryptedPayload)
    {
        if (encryptedPayload.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted payload is too short.");

        var nonce = encryptedPayload[..NonceSize];
        var tag = encryptedPayload.Slice(NonceSize, TagSize);
        var ciphertext = encryptedPayload[(NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
