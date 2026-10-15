namespace AIToolbox.Ductwork.Contracts;

/// <summary>
/// Version of the wire contract between the web app and any worker. A worker that does not speak
/// the web app's major version is refused at the handshake.
/// </summary>
public static class ContractInfo
{
    public const string Version = "0.1.0";

    public static int Major => int.Parse(Version.Split('.')[0]);
}
