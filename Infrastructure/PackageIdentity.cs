using System.Runtime.InteropServices;

namespace SoundBridge.Infrastructure;

/// <summary>
/// 判断当前是否以 MSIX 打包身份运行（商店版 / 侧载版）。
/// 打包后注册表、AppData 都会被虚拟化，一些行为必须走另一条路。
/// </summary>
internal static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            var length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return result != AppModelErrorNoPackage;
        }
        catch
        {
            return false;
        }
    }
}
