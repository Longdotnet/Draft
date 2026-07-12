using System.Security.Cryptography;
using System.Text;

namespace VolleyDraft.Api.Services;

public sealed class ZaloCredentialProtector(IConfiguration configuration)
{
    private readonly byte[] key = BuildKey(configuration);

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedValue)
    {
        var payload = Convert.FromBase64String(protectedValue);
        if (payload.Length < 29)
        {
            throw new CryptographicException("Invalid encrypted Zalo credential payload.");
        }

        var nonce = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(12, 16);
        var ciphertext = payload.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] BuildKey(IConfiguration configuration)
    {
        var configured = configuration["Zalo:CredentialEncryptionKey"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var decoded = Convert.FromBase64String(configured);
                if (decoded.Length == 32)
                {
                    return decoded;
                }
            }
            catch (FormatException)
            {
                // Fall through to deterministic hashing for plain-text development keys.
            }

            return SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        }

        var fallback = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Zalo:CredentialEncryptionKey or Jwt:Key is required.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(fallback));
    }
}
