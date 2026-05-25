namespace StringifyDesktop.Services;

public static class ProtectedFileStoreFactory
{
    public static IProtectedFileStore Create(AppPaths paths)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProtectedFileStore(paths);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxProtectedFileStore();
        }

        throw new PlatformNotSupportedException("Secure storage is only implemented for Windows and Linux.");
    }
}

