// Generated from docs/address-migrations/2026-07-29-wingdk.md.
// Research seeds only: inclusion does not make a hook safe to invoke.
#pragma once

#include <array>
#include <cstdint>

namespace halo_meister_research
{
enum class Confidence
{
    exact,
    content_match,
    pointer_slot,
    interpolated,
};

struct AddressMigration
{
    const char* name;
    std::uintptr_t old_rva;
    std::uintptr_t new_rva;
    Confidence confidence;
};

inline constexpr std::array<AddressMigration, 61> kAddressMigrations{{
    {"HEAP_BASE", 0x00AA1978, 0x00AA0958, Confidence::exact},
    {"kAiLivingCountRva", 0x000FB0B0, 0x000FB0C0, Confidence::exact},
    {"kCampaignEngineInitializeRva", 0x002C2310, 0x002C2320, Confidence::exact},
    {"kCampaignEngineObjectRva", 0x009CCD88, 0x009CBDC0, Confidence::pointer_slot},
    {"kCampaignVariantVtableRva", 0x0083D1E0, 0x0083C170, Confidence::exact},
    {"kCinematicSkipTestCallRva", 0x001E1375, 0x001E1385, Confidence::exact},
    {"kCinematicStartCallRva", 0x001E3618, 0x001E3628, Confidence::interpolated},
    {"kDatumArrayNextRva", 0x00044570, 0x00044570, Confidence::interpolated},
    {"kDatumIteratorNextRva", 0x000444E0, 0x000444E0, Confidence::exact},
    {"kEngineObjectTableRva", 0x00BD6230, 0x00BD5210, Confidence::exact},
    {"kGameEngineSelectorRva", 0x00180E60, 0x00180E70, Confidence::exact},
    {"kGameEngineShutdownRva", 0x002ADA40, 0x002ADA50, Confidence::exact},
    {"kGameInitializeRva", 0x002ACA10, 0x002ACA20, Confidence::exact},
    {"kHsThreadDeleteRva", 0x001FFE60, 0x001FFE70, Confidence::interpolated},
    {"kMachinimaQueryRva", 0x00276460, 0x00276470, Confidence::interpolated},
    {"kMachinimaToggleRva", 0x0026D530, 0x0026D540, Confidence::interpolated},
    {"kMegaloEngineInitializeRva", 0x003FD530, 0x003FD540, Confidence::exact},
    {"kMegaloEngineObjectRva", 0x00C7B7D8, 0x00C7A7B8, Confidence::pointer_slot},
    {"kMegaloVariantVtableRva", 0x0083D428, 0x0083C3B8, Confidence::exact},
    {"kObjectGetOrientationRva", 0x005A7580, 0x005A6AD0, Confidence::exact},
    {"kObjectGetPositionRva", 0x005A7220, 0x005A6770, Confidence::interpolated},
    {"kObjectNewRva", 0x005A1A60, 0x005A0FB0, Confidence::exact},
    {"kObjectPlacementDataNewRva", 0x005EEED0, 0x005EE570, Confidence::exact},
    {"kObjectPredeleteRva", 0x005B4B30, 0x005B4080, Confidence::exact},
    {"kObjectivesClearRva", 0x00480A10, 0x00480A20, Confidence::exact},
    {"kPlaceSquadRva", 0x000FD800, 0x000FD810, Confidence::exact},
    {"kSandboxEngineInitializeRva", 0x00300CC0, 0x00300CD0, Confidence::exact},
    {"kSandboxEngineObjectRva", 0x00BD62F0, 0x00BD52D0, Confidence::pointer_slot},
    {"kSandboxVariantVtableRva", 0x0083D128, 0x0083C470, Confidence::exact},
    {"kScenarioAiGlobalRva", 0x010C4550, 0x010C3558, Confidence::exact},
    {"kScenarioGlobalRva", 0x010C4550, 0x010C3558, Confidence::exact},
    {"kScenarioObjectDeleteRva", 0x00350940, 0x00350950, Confidence::interpolated},
    {"kSetGameEngineIndexRva", 0x002164D0, 0x002164E0, Confidence::exact},
    {"kSimulationPulseRva", 0x001B05F0, 0x001B0600, Confidence::exact},
    {"kSurvivalDefaultsRva", 0x0039F260, 0x0039F270, Confidence::exact},
    {"kSurvivalEngineDisposeRva", 0x0029EAB0, 0x0029EAC0, Confidence::interpolated},
    {"kSurvivalEngineInitializeRva", 0x0029EA30, 0x0029EA40, Confidence::exact},
    {"kSurvivalEngineObjectRva", 0x009B2F78, 0x009B1F78, Confidence::pointer_slot},
    {"kSurvivalNullCheckResumeRva", 0x0029F3CF, 0x0029F3DF, Confidence::interpolated},
    {"kSurvivalNullCheckRva", 0x0029F3C0, 0x0029F3D0, Confidence::interpolated},
    {"kSurvivalNullCheckSkipRva", 0x0029F444, 0x0029F454, Confidence::interpolated},
    {"kSurvivalVariantGlobalRva", 0x02C30718, 0x02C2F710, Confidence::exact},
    {"kSurvivalVariantVtableRva", 0x0083D2B8, 0x0083C248, Confidence::exact},
    {"kTagResolverTableRva", 0x02C2DCC0, 0x02C2CCC0, Confidence::exact},
    {"kTagTablePtrRva", 0x0182E1E8, 0x0182D1E8, Confidence::exact},
    {"kTlsIndexRva", 0x00D73730, 0x00D72730, Confidence::exact},
    {"kUnitAcquireObjectRva", 0x0060A520, 0x00609BC0, Confidence::exact},
    {"kUnitAddEquipmentRva", 0x006099E0, 0x00609080, Confidence::exact},
    {"kUnitDropWeaponsRva", 0x0060A150, 0x006097F0, Confidence::exact},
    {"kWeaponPickupActionExecutorRva", 0x0065C560, 0x0065BC80, Confidence::exact},
    {"kWeaponPickupEligibilityRva", 0x0060AFE0, 0x0060A680, Confidence::exact},
    {"kWeaponPickupModeRva", 0x0060BFA0, 0x0060B640, Confidence::exact},
    {"ALLOC", 0x000430B0, 0x000430B0, Confidence::exact},
    {"POOL_TABLE", 0x00AA18E0, 0x00AA08C0, Confidence::exact},
    {"RESOLVER_TABLE_RVA", 0x02C2DCC0, 0x02C2CCC0, Confidence::exact},
    {"RVA_POOL0_SIZE_A", 0x00AA1908, 0x00AA08E8, Confidence::content_match},
    {"RVA_POOL0_SIZE_B", 0x00AA1910, 0x00AA08F0, Confidence::exact},
    {"RVA_POOL1_SIZE_B", 0x00AA1958, 0x00AA0938, Confidence::exact},
    {"RVA_RESERVE_IMM", 0x00042DC5, 0x00042DC5, Confidence::exact},
    {"TAG_TABLE_PTR_RVA", 0x0182E1E8, 0x0182D1E8, Confidence::exact},
    {"TLS_INDEX_RVA", 0x00D73730, 0x00D72730, Confidence::exact},
}};
} // namespace halo_meister_research
