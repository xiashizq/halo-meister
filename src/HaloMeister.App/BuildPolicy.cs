namespace HaloMeister.App;

internal static class BuildPolicy
{
#if RETAIL
    public static bool IsRetail { get; } = true;
#else
    public static bool IsRetail { get; } = false;
#endif

    public static bool EnforceCustomizationOwnership => IsRetail;
}
