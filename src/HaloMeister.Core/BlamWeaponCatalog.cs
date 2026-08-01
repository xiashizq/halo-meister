namespace HaloMeister.Core;

/// <summary>
/// Player-usable weapon blueprints. Every path here was resolved against a
/// running Campaign Evolved build with <c>StaticFindObject</c>, so the asset
/// exists — note that the two content roots are not interchangeable.
///
/// First-person (<c>BP_FP_*</c>) variants, cinematic rigs, turrets and vehicle
/// mounts are deliberately excluded: they are not weapons a unit carries.
/// </summary>
public sealed record BlamWeapon(string DisplayName, string AssetPath, string ClassName)
{
    public string AssetName => AssetPath[(AssetPath.LastIndexOf('/') + 1)..];
}

public static class BlamWeaponCatalog
{
    private const string Sync = "/Game/Blueprints/Synchronization/Weapons/";
    private const string Proto = "/Game/_Prototypes/SynchronizationTestContent/Assets/Weapons/WeaponActors/";

    public static IReadOnlyList<BlamWeapon> All { get; } =
    [
        Make("Assault Rifle", Sync, "BP_AssaultRifle_WeaponActor"),
        Make("Battle Rifle", Sync, "BP_BattleRifle_WeaponActor"),
        Make("Beam Rifle", Sync, "BP_BeamRifle_WeaponActor"),
        Make("Concussion Rifle", Proto, "BP_ConcussionRifle_WeaponActor"),
        Make("DMR", Proto, "BP_DMR_WeaponActor"),
        Make("Energy Sword", Sync, "BP_EnergySword_WeaponActor"),
        Make("Flak Cannon", Sync, "BP_FlakCannon_WeaponActor"),
        Make("Fuel Rod Cannon", Proto, "BP_Hunter_FuelRod_WeaponActor"),
        Make("Magnum", Sync, "BP_Magnum_WeaponActor"),
        Make("Needle Rifle", Sync, "BP_NeedleRifleWeaponActor"),
        Make("Needler", Proto, "BP_Needler_WeaponActor"),
        Make("Plasma Pistol", Sync, "BP_PlasmaPistol_WeaponActor"),
        Make("Plasma Rifle", Sync, "BP_PlasmaRifle_WeaponActor"),
        Make("Plasma Rifle (Red)", Sync, "BP_PlasmaRifle_Red_WeaponActor"),
        Make("Rocket Launcher", Sync, "BP_RocketLauncher_WeaponActor"),
        Make("SMG", Sync, "BP_SMG_WeaponActor"),
        Make("Sentinel Beam", Sync, "BP_SentinelBeamWeaponActor"),
        Make("Shotgun", Sync, "BP_Shotgun_WeaponActor"),
        Make("Sniper Rifle", Sync, "BP_SniperRifle_WeaponActor"),
        Make("Spike Rifle", Sync, "BP_SpikeRifle_WeaponActor"),
        Make("Stanchion", Sync, "BP_Stanchion_WeaponActor"),
        Make("Unarmed", Proto, "BP_Unarmed_WeaponActor"),
    ];

    public static BlamWeapon? Find(string nameOrAsset)
    {
        if (string.IsNullOrWhiteSpace(nameOrAsset)) return null;
        string trimmed = nameOrAsset.Trim();
        return All.FirstOrDefault(weapon =>
                   weapon.AssetName.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(weapon =>
                   weapon.DisplayName.Replace(" ", "").Equals(
                       trimmed.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Matches an actor's stored class name back to a catalog entry.</summary>
    public static BlamWeapon? FromClassName(string? className)
        => className is null
            ? null
            : All.FirstOrDefault(weapon =>
                weapon.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase));

    private static BlamWeapon Make(string display, string directory, string asset)
        => new(display, directory + asset, asset + "_C");
}
