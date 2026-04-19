using System.Security.Cryptography;
using System.Text;

namespace WordGame.Server.Security;

public class MessageCrypto : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string ExportPublicKey()
    {
        return Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());
    }

    public string ComputePayloadHash(string payloadJson)
    {
        var bytes = Encoding.UTF8.GetBytes(payloadJson);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public string SignHash(string payloadHash)
    {
        var bytes = Encoding.UTF8.GetBytes(payloadHash);
        var signature = _rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signature);
    }

    public bool VerifyHash(string payloadHash, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureBase64);
            var publicKey = Convert.FromBase64String(publicKeyBase64);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return rsa.VerifyData(
                Encoding.UTF8.GetBytes(payloadHash),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _rsa.Dispose();
    }
}
