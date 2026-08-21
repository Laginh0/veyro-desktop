namespace Veyro.Desktop.Core;

public sealed record AppPaths(
    string DataDirectory,
    string IdentityFile,
    string IdentityKeyFile,
    string TrustFile,
    string LogDirectory)
{
    public string FeaturePermissionsFile => Path.Combine(DataDirectory, "feature-permissions.dat");

    public string IncomingFilesDirectory => Path.Combine(DataDirectory, "Received Files");

    public static AppPaths CreateDefault()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDirectory = Path.Combine(localData, "Veyro");
        return new AppPaths(
            dataDirectory,
            Path.Combine(dataDirectory, "identity.dat"),
            Path.Combine(dataDirectory, "identity-key.dat"),
            Path.Combine(dataDirectory, "trusted-devices.dat"),
            Path.Combine(dataDirectory, "logs"));
    }
}
