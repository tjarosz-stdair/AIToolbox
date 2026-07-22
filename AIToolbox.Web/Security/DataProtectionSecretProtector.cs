using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace AIToolbox.Web.Security;

public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("AITools.ApiCredentials.v1");
    }

    public string? Protect(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : _protector.Protect(value);
    }

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "The stored API credential could not be decrypted. Configure a persistent DataProtection:KeysPath, recycle the app, then re-enter this provider's API key.",
                ex);
        }
    }
}
