using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.BatToExe;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class BatPayloadCodecTests
{
    private static readonly byte[] Sample = Encoding.UTF8.GetBytes("@echo off\r\nsetlocal\r\necho hello world\r\n");

    [Test]
    public void Encode_UsesExpectedModeStrings()
    {
        Assert.That(BatPayloadCodec.Encode(Sample, compress: false, passphrase: null, encrypt: false).Mode,
            Is.EqualTo(BatPayloadCodec.ModeRaw));
        Assert.That(BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: false).Mode,
            Is.EqualTo(BatPayloadCodec.ModeGzip));
        Assert.That(BatPayloadCodec.Encode(Sample, compress: false, passphrase: "pw", encrypt: false).Mode,
            Is.EqualTo(BatPayloadCodec.ModeRawXor));
        Assert.That(BatPayloadCodec.Encode(Sample, compress: true, passphrase: "pw", encrypt: false).Mode,
            Is.EqualTo(BatPayloadCodec.ModeGzipXor));
        Assert.That(BatPayloadCodec.Encode(Sample, compress: false, passphrase: null, encrypt: true).Mode,
            Is.EqualTo(BatPayloadCodec.ModeRawAes));
        Assert.That(BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: true).Mode,
            Is.EqualTo(BatPayloadCodec.ModeGzipAes));
        Assert.That(BatPayloadCodec.Encode(Sample, compress: false, passphrase: "pw", encrypt: true).Mode,
            Is.EqualTo(BatPayloadCodec.ModeRawXorAes));
        Assert.That(BatPayloadCodec.Encode(Sample, compress: true, passphrase: "pw", encrypt: true).Mode,
            Is.EqualTo(BatPayloadCodec.ModeGzipXorAes));
    }

    [Test]
    public void ModeHelpers_RecognizeEveryLayer()
    {
        Assert.That(BatPayloadCodec.IsCompressed(BatPayloadCodec.ModeGzipXorAes), Is.True);
        Assert.That(BatPayloadCodec.IsXorObfuscated(BatPayloadCodec.ModeGzipXorAes), Is.True);
        Assert.That(BatPayloadCodec.IsAesEncrypted(BatPayloadCodec.ModeGzipXorAes), Is.True);

        Assert.That(BatPayloadCodec.IsCompressed(BatPayloadCodec.ModeRawAes), Is.False);
        Assert.That(BatPayloadCodec.IsXorObfuscated(BatPayloadCodec.ModeRawAes), Is.False);
        Assert.That(BatPayloadCodec.IsAesEncrypted(BatPayloadCodec.ModeRawAes), Is.True);

        Assert.That(BatPayloadCodec.IsCompressed(BatPayloadCodec.ModeGzip), Is.True);
        Assert.That(BatPayloadCodec.IsXorObfuscated(BatPayloadCodec.ModeGzip), Is.False);
        Assert.That(BatPayloadCodec.IsAesEncrypted(BatPayloadCodec.ModeGzip), Is.False);

        Assert.That(BatPayloadCodec.IsXorObfuscated(BatPayloadCodec.ModeGzipXor), Is.True);
        Assert.That(BatPayloadCodec.IsAesEncrypted(BatPayloadCodec.ModeGzipXor), Is.False);
    }

    [Test]
    public void Encode_Output_IsBase64()
    {
        foreach (var compress in new[] { false, true })
        {
            foreach (var passphrase in new[] { (string?)null, "pw" })
            {
                foreach (var encrypt in new[] { false, true })
                {
                    var payload = BatPayloadCodec.Encode(Sample, compress, passphrase, encrypt);
                    Assert.That(payload.Data, Does.Match("^[A-Za-z0-9+/]*={0,2}$"),
                        $"mode={payload.Mode}");
                }
            }
        }
    }

    [Test]
    public void Encode_WithAes_UsesFreshKeyEveryTime()
    {
        var first = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: true);
        var second = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: true);
        Assert.That(first.Data, Is.Not.EqualTo(second.Data), "each build must get a random AES key");
    }

    [Test]
    public void Roundtrip_Raw()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: false, passphrase: null, encrypt: false);
        Assert.That(BatPayloadCodec.Decode(payload, null), Is.EqualTo(Sample));
    }

    [Test]
    public void Roundtrip_Gzip()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: false);
        Assert.That(BatPayloadCodec.Decode(payload, null), Is.EqualTo(Sample));
    }

    [Test]
    public void Roundtrip_RawXor()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: false, passphrase: "s3cret!", encrypt: false);
        Assert.That(BatPayloadCodec.Decode(payload, "s3cret!"), Is.EqualTo(Sample));
    }

    [Test]
    public void Roundtrip_GzipXor()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: true, passphrase: "s3cret!", encrypt: false);
        Assert.That(BatPayloadCodec.Decode(payload, "s3cret!"), Is.EqualTo(Sample));
    }

    [Test]
    public void Roundtrip_RawAes()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: false, passphrase: null, encrypt: true);
        Assert.That(BatPayloadCodec.Decode(payload, null), Is.EqualTo(Sample));
    }

    [Test]
    public void Roundtrip_GzipAes()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: true);
        Assert.That(BatPayloadCodec.Decode(payload, null), Is.EqualTo(Sample));
    }

    [Test]
    public void Roundtrip_RawXorAes()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: false, passphrase: "s3cret!", encrypt: true);
        Assert.That(BatPayloadCodec.Decode(payload, "s3cret!"), Is.EqualTo(Sample));
    }

    [Test]
    public void Roundtrip_GzipXorAes()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: true, passphrase: "s3cret!", encrypt: true);
        Assert.That(BatPayloadCodec.Decode(payload, "s3cret!"), Is.EqualTo(Sample));
    }

    [Test]
    public void Decode_AesTamperedCiphertext_Throws()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: true);
        var raw = Convert.FromBase64String(payload.Data);
        raw[raw.Length - 1] ^= 0xFF;
        var tampered = new BatPayload(payload.Mode, Convert.ToBase64String(raw), payload.AesSecret);
        var ex = Assert.Throws<InvalidDataException>(() => BatPayloadCodec.Decode(tampered, null));
        Assert.That(ex!.Message, Does.Contain("corrupted"));
    }

    [Test]
    public void Decode_AesTruncatedPayload_Throws()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: true);
        var raw = Convert.FromBase64String(payload.Data);
        var truncated = new BatPayload(payload.Mode, Convert.ToBase64String(raw, 0, 40), payload.AesSecret);
        var ex = Assert.Throws<InvalidDataException>(() => BatPayloadCodec.Decode(truncated, null));
        Assert.That(ex!.Message, Does.Contain("truncated or corrupted"));
    }

    [Test]
    public void Decode_AesWithoutSecret_Throws()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: true);
        var withoutKey = new BatPayload(payload.Mode, payload.Data);
        var ex = Assert.Throws<InvalidDataException>(() => BatPayloadCodec.Decode(withoutKey, null));
        Assert.That(ex!.Message, Does.Contain("no decryption key"));
    }

    [Test]
    public void Encode_WithAes_DoesNotEmbedTheKeyInTheBlob()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: true);
        var raw = Convert.FromBase64String(payload.Data);

        Assert.That(payload.AesSecret, Is.Not.Null);
        Assert.That(payload.AesSecret!.Length, Is.EqualTo(32));
        Assert.That(IndexOf(raw, payload.AesSecret), Is.EqualTo(-1),
            "the AES key must never appear inside the payload blob");

        // Header layout: 16-byte random salt | 16-byte IV | ciphertext.
        Assert.That(raw.Length, Is.GreaterThanOrEqualTo(48));
        Assert.That((raw.Length - 32) % 16, Is.EqualTo(0));
        var header = new byte[32];
        Buffer.BlockCopy(raw, 0, header, 0, 32);
        Assert.That(header, Is.Not.EqualTo(payload.AesSecret));
    }

    [Test]
    public void Encode_WithAes_UsesCallerSecret()
    {
        var callerSecret = BatPayloadCodec.CreateAesSecret();
        var payload = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null,
            encrypt: true, aesSecret: callerSecret);

        Assert.That(payload.AesSecret, Is.EqualTo(callerSecret));
        Assert.That(BatPayloadCodec.Decode(payload, null), Is.EqualTo(Sample));

        var wrongSecret = BatPayloadCodec.CreateAesSecret();
        Assert.Throws<InvalidDataException>(() =>
            BatPayloadCodec.Decode(payload.Mode, payload.Data, null, wrongSecret));
    }

    [Test]
    public void Encode_WithAes_NullSecret_GeneratesOneAndExposesItInPayload()
    {
        var first = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: true);
        var second = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: true);

        Assert.That(first.AesSecret, Is.Not.Null);
        Assert.That(first.AesSecret, Has.Length.EqualTo(32));
        Assert.That(second.AesSecret, Is.Not.Null);
        Assert.That(second.AesSecret, Has.Length.EqualTo(32));
        Assert.That(first.AesSecret, Is.Not.EqualTo(second.AesSecret));
    }

    [Test]
    public void Encode_WithoutAes_AesSecretIsNull()
    {
        Assert.That(BatPayloadCodec.Encode(Sample, compress: true, passphrase: null, encrypt: false).AesSecret,
            Is.Null);
        Assert.That(BatPayloadCodec.Encode(Sample, compress: true, passphrase: "pw", encrypt: false).AesSecret,
            Is.Null);
    }

    [Test]
    public void Encode_WithAes_SameSecretDifferentSalt_ProducesDifferentData()
    {
        var secret = BatPayloadCodec.CreateAesSecret();
        var first = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null,
            encrypt: true, aesSecret: secret);
        var second = BatPayloadCodec.Encode(Sample, compress: true, passphrase: null,
            encrypt: true, aesSecret: secret);

        Assert.That(second.Data, Is.Not.EqualTo(first.Data),
            "each encode must draw a fresh salt/IV so identical secrets still differ");
        Assert.That(BatPayloadCodec.Decode(second, null), Is.EqualTo(Sample));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    [Test]
    public void Gzip_ShrinksRepetitiveData()
    {
        var repetitive = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abcd", 2048)));
        var raw = BatPayloadCodec.Encode(repetitive, compress: false, passphrase: null, encrypt: false);
        var gz = BatPayloadCodec.Encode(repetitive, compress: true, passphrase: null, encrypt: false);
        Assert.That(gz.Data.Length, Is.LessThan(raw.Data.Length));
    }

    [Test]
    public void Decode_GzipXor_WrongPassphrase_Throws()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: true, passphrase: "right", encrypt: false);
        var ex = Assert.Throws<InvalidDataException>(
            () => BatPayloadCodec.Decode(payload, "wrong"));
        Assert.That(ex!.Message, Does.Contain("passphrase is wrong or the data is corrupted"));
    }

    [Test]
    public void Decode_ObfuscatedWithoutPassphrase_Throws()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: false, passphrase: "pw", encrypt: false);
        var ex = Assert.Throws<InvalidDataException>(() => BatPayloadCodec.Decode(payload, null));
        Assert.That(ex!.Message, Does.Contain("passphrase"));
    }

    [Test]
    public void Decode_RawXor_WrongPassphrase_ReturnsDifferentBytes()
    {
        var payload = BatPayloadCodec.Encode(Sample, compress: false, passphrase: "right", encrypt: false);
        var decoded = BatPayloadCodec.Decode(payload, "wrong");
        Assert.That(decoded, Is.Not.EqualTo(Sample));
    }

    [Test]
    public void Decode_InvalidBase64_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => BatPayloadCodec.Decode(BatPayloadCodec.ModeRaw, "not base64!!", null));
        Assert.That(ex!.Message, Does.Contain("Base64"));
    }

    [Test]
    public void Decode_UnknownMode_Throws()
    {
        var data = Convert.ToBase64String(Sample);
        var ex = Assert.Throws<InvalidDataException>(
            () => BatPayloadCodec.Decode("bogus", data, null));
        Assert.That(ex!.Message, Does.Contain("Unknown payload encoding mode"));
    }

    [Test]
    public void Decode_EmptyData_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => BatPayloadCodec.Decode(BatPayloadCodec.ModeRaw, string.Empty, null));
    }

    [Test]
    public void Decode_EmptyMode_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => BatPayloadCodec.Decode("  ", Convert.ToBase64String(Sample), null));
    }
}
