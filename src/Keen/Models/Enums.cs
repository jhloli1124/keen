namespace Keen.Models;

internal enum FileHealth
{
    Watching,
    Syncing,
    Degraded,
    Failing,
    Missing,
    Paused,
}

internal enum VersionKind
{
    Normal = 0,
    PreRestoreSnapshot = 1,
    SyntheticRestore = 2,
}
