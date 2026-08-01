-- Halo: Campaign Evolved / Meteorite, UE 5.5
-- Game build 5.5.4-1097863, Meteorite-2606-CU2.
-- Unique function-entry signature for FName::FName(wchar_t*, EFindName).
-- Verified against HaloCampaignEvolved.exe at RVA 0x36FD0E0.
function Register()
    return "48 89 5C 24 08 57 48 83 EC 30 41 8B F8 48 89 54"
end

function OnMatchFound(MatchAddress)
    return MatchAddress
end
