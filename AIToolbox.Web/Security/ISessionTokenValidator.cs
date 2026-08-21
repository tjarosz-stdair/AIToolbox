namespace AIToolbox.Web.Security;

public interface ISessionTokenValidator
{
    /// <summary>
    /// Cryptographically validates a P21 Toolbox session token. AIToolbox must call this before
    /// doing any AI-provider-spending work with the token present — never just forward it to P21
    /// Toolbox unvalidated, or a fabricated token could be used to consume AI spend under a false
    /// identity. Absence of a token is not itself an error (see SessionTokenValidationResult.Absent).
    /// </summary>
    Task<SessionTokenValidationResult> ValidateAsync(string? rawToken, CancellationToken cancellationToken = default);
}
