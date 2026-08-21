namespace Veyro.Desktop.Core.Features;

public enum VeyroFeature
{
    Files = 1,
    Clipboard = 2,
    Links = 3,
    Notifications = 4,
    MediaControl = 5,
    SecureCommands = 6,
    Presentation = 7,
    RemoteInput = 8,
    SharedFolders = 9
}

public enum FeatureAccessPolicy
{
    Disabled = 0,
    Ask = 1,
    Allow = 2
}
