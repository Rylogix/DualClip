using System.Runtime.InteropServices;
using System.Text;

namespace DualClip.Infrastructure;

public static class AppRuntimeInfo
{
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsPackaged => TryGetCurrentPackageFullName(out _);

    public static bool TryGetCurrentPackageFullName(out string packageFullName)
    {
        var length = 0;
        var result = GetCurrentPackageFullName(ref length, null);

        if (result == AppModelErrorNoPackage)
        {
            packageFullName = string.Empty;
            return false;
        }

        if (result != 0 || length <= 0)
        {
            packageFullName = string.Empty;
            return false;
        }

        var builder = new StringBuilder(length);
        result = GetCurrentPackageFullName(ref length, builder);

        if (result != 0)
        {
            packageFullName = string.Empty;
            return false;
        }

        packageFullName = builder.ToString();
        return !string.IsNullOrWhiteSpace(packageFullName);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}
