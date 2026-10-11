using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ProPS1Studio.Core.BatToExe;

/// <summary>
/// Encodes the batch payload (and bundled files) for embedding inside the generated executable:
/// raw bytes -&gt; optional GZip -&gt; optional XOR (SHA-256 passphrase key) -&gt; optional AES-256-CBC
/// (random per-conversion key kept OUTSIDE the blob; the blob only carries a 16-byte random salt
/// and a 16-byte IV: salt|iv|ciphertext) -&gt; Base64.
/// Mode grammar: "raw"|"gz" + optional "x" (XOR) + optional "a" (AES), e.g. "gz", "gzx", "gza", "gzxa".
///
/// The AES key is never stored next to the ciphertext. The caller generates it once per
/// conversion via <see cref="CreateAesSecret"/> and receives it back on
/// <see cref="BatPayload.AesSecret"/>; the launcher generator XOR-maskifies it into the compiled
/// source instead of embedding it in the payload blob.
/// </summary>
public static class BatPayloadCodec
{
    public const string ModeRaw = "raw";
    public const string ModeGzip = "gz";
    public const string ModeRawXor = "rawx";
    public const string ModeGzipXor = "gzx";
    public const string ModeRawAes = "rawa";
    public const string ModeGzipAes = "gza";
    public const string ModeRawXorAes = "rawxa";
    public const string ModeGzipXorAes = "gzxa";

    private const string Salt = "ProPS1.BatToExe.v1";
    private const int AesKeySizeBytes = 32;
    private const int AesSaltSizeBytes = 16;
    private const int AesIvSizeBytes = 16;

    /// <summary>salt (16) + IV (16) prefix every AES blob; ciphertext follows.</summary>
    private const int AesHeaderSizeBytes = AesSaltSizeBytes + AesIvSizeBytes;

    /// <summary>Creates a fresh random 256-bit AES secret for one conversion.</summary>
    public static byte[] CreateAesSecret() => RandomNumberGenerator.GetBytes(AesKeySizeBytes);

    /// <summary>Encodes raw payload bytes. Returns the mode string plus Base64 data.
    /// When <paramref name="encrypt"/> is true the AES key is taken from
    /// <paramref name="aesSecret"/> (or generated when null) and returned on
    /// <see cref="BatPayload.AesSecret"/> - never inside the Base64 blob.</summary>
    public static BatPayload Encode(byte[] data, bool compress, string? passphrase, bool encrypt, byte[]? aesSecret = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        var buffer = data;
        if (compress)
        {
            buffer = GZip(buffer);
        }

        var obfuscate = !string.IsNullOrEmpty(passphrase);
        if (obfuscate)
        {
            buffer = (byte[])buffer.Clone();
            XorInPlace(buffer, DeriveKey(passphrase!));
        }

        byte[]? secret = null;
        if (encrypt)
        {
            secret = aesSecret ?? CreateAesSecret();
            buffer = AesEncrypt(buffer, secret);
        }

        var mode = compress ? ModeGzip : ModeRaw;
        if (obfuscate)
        {
            mode += "x";
        }

        if (encrypt)
        {
            mode += "a";
        }

        return new BatPayload(mode, Convert.ToBase64String(buffer), secret);
    }

    /// <summary>True when the mode ends with the AES layer ("a" suffix).</summary>
    public static bool IsAesEncrypted(string mode) =>
        !string.IsNullOrEmpty(mode) && mode.EndsWith("a", StringComparison.Ordinal);

    /// <summary>True when the mode carries the XOR layer ("x" before an optional "a" suffix).</summary>
    public static bool IsXorObfuscated(string mode)
    {
        var core = StripSuffix(mode, "a");
        return core.EndsWith("x", StringComparison.Ordinal);
    }

    /// <summary>True when the mode carries the GZip layer.</summary>
    public static bool IsCompressed(string mode)
    {
        var core = StripSuffix(StripSuffix(mode, "a"), "x");
        return core == ModeGzip;
    }

    public static byte[] Decode(BatPayload payload, string? passphrase)
        => Decode(payload.Mode, payload.Data, passphrase, payload.AesSecret);

    /// <summary>Decodes Base64 payload data. For AES modes <paramref name="aesSecret"/> is
    /// mandatory - the blob never contains the key.</summary>
    public static byte[] Decode(string mode, string data, string? passphrase, byte[]? aesSecret = null)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            throw new InvalidDataException("The embedded payload has no encoding mode.");
        }

        if (string.IsNullOrEmpty(data))
        {
            throw new InvalidDataException("The embedded payload is empty.");
        }

        byte[] buffer;
        try
        {
            buffer = Convert.FromBase64String(data);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The embedded payload is not valid Base64.", ex);
        }

        ParseMode(mode, out var compressed, out var obfuscated, out var encrypted);

        if (encrypted)
        {
            if (aesSecret is null || aesSecret.Length == 0)
            {
                throw new InvalidDataException("The payload is encrypted but no decryption key was supplied.");
            }

            buffer = AesDecrypt(buffer, aesSecret);
        }

        if (obfuscated)
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                throw new InvalidDataException("The payload is passphrase-protected but no passphrase was supplied.");
            }

            XorInPlace(buffer, DeriveKey(passphrase));
        }

        if (compressed)
        {
            try
            {
                buffer = UnGZip(buffer);
            }
            catch (InvalidDataException)
            {
                throw new InvalidDataException(
                    "The payload could not be decompressed: the passphrase is wrong or the data is corrupted.");
            }
        }

        if (buffer.Length == 0)
        {
            throw new InvalidDataException("The decoded payload is empty: the passphrase is wrong or the data is corrupted.");
        }

        return buffer;
    }

    private static void ParseMode(string mode, out bool compressed, out bool obfuscated, out bool encrypted)
    {
        compressed = false;
        obfuscated = false;
        encrypted = false;

        var core = mode;
        if (core.EndsWith("a", StringComparison.Ordinal))
        {
            encrypted = true;
            core = core.Substring(0, core.Length - 1);
        }

        if (core.EndsWith("x", StringComparison.Ordinal))
        {
            obfuscated = true;
            core = core.Substring(0, core.Length - 1);
        }

        if (core == ModeGzip)
        {
            compressed = true;
        }
        else if (core != ModeRaw)
        {
            throw new InvalidDataException($"Unknown payload encoding mode: {mode}.");
        }
    }

    private static string StripSuffix(string value, string suffix) =>
        !string.IsNullOrEmpty(value) && value.EndsWith(suffix, StringComparison.Ordinal)
            ? value.Substring(0, value.Length - suffix.Length)
            : value;

    private static byte[] AesEncrypt(byte[] data, byte[] secret)
    {
        var salt = RandomNumberGenerator.GetBytes(AesSaltSizeBytes);
        var iv = RandomNumberGenerator.GetBytes(AesIvSizeBytes);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = DeriveAesKey(secret, salt);
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(data, 0, data.Length);

        var result = new byte[AesHeaderSizeBytes + ciphertext.Length];
        Buffer.BlockCopy(salt, 0, result, 0, AesSaltSizeBytes);
        Buffer.BlockCopy(iv, 0, result, AesSaltSizeBytes, AesIvSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, AesHeaderSizeBytes, ciphertext.Length);
        return result;
    }

    private static byte[] AesDecrypt(byte[] data, byte[] secret)
    {
        if (data.Length < AesHeaderSizeBytes + 16 || (data.Length - AesHeaderSizeBytes) % 16 != 0)
        {
            throw new InvalidDataException("The encrypted payload is truncated or corrupted.");
        }

        var salt = new byte[AesSaltSizeBytes];
        var iv = new byte[AesIvSizeBytes];
        Buffer.BlockCopy(data, 0, salt, 0, AesSaltSizeBytes);
        Buffer.BlockCopy(data, AesSaltSizeBytes, iv, 0, AesIvSizeBytes);

        try
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = DeriveAesKey(secret, salt);
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, AesHeaderSizeBytes, data.Length - AesHeaderSizeBytes);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("The payload could not be decrypted: the data is corrupted.", ex);
        }
    }

    /// <summary>key = SHA-256(secret || salt). The salt travels inside the blob; the secret never does.</summary>
    private static byte[] DeriveAesKey(byte[] secret, byte[] salt)
    {
        var material = new byte[secret.Length + salt.Length];
        Buffer.BlockCopy(secret, 0, material, 0, secret.Length);
        Buffer.BlockCopy(salt, 0, material, secret.Length, salt.Length);
        return SHA256.HashData(material);
    }

    private static byte[] DeriveKey(string passphrase)
    {
        var material = Encoding.UTF8.GetBytes(passphrase + Salt);
        return SHA256.HashData(material);
    }

    private static void XorInPlace(byte[] buffer, byte[] key)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(buffer[i] ^ key[i % key.Length]);
        }
    }

    private static byte[] GZip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[] UnGZip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
