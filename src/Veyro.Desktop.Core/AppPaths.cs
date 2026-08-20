namespace Veyro.Desktop.Core;

public sealed record AppPaths(string DataDirectory, string IdentityFile, string LogDirectory)
{
    public static AppPaths CreateDefault()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDirectory = Path.Combine(localData, "Veyro");
        return new AppPaths(
            dataDirectory,
            Path.Combine(dataDirectory, "identity.dat"),
            Path.Combine(dataDirectory, "logs"));
    }
}
