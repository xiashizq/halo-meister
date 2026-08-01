-- Halo: Campaign Evolved / Meteorite, UE 5.5
-- Verified against HaloCampaignEvolved.exe: single match at RVA 0x379C0AB,
-- resolving to GUObjectArray RVA 0xD0B8770.
function Register()
    return "48 8D ?? ?? ?? ?? ?? 4C 8B C9 48 89 01 B8 FF FF FF FF 89 41 08 0F 45 ?? ?? ?? ?? ?? FF C0 89 41 08 3B"
end

function OnMatchFound(MatchAddress)
    local LeaInstr = MatchAddress
    local NextInstr = LeaInstr + 0x7
    local Offset = LeaInstr + 0x3
    local AddressLoaded = NextInstr + DerefToInt32(Offset)
    return AddressLoaded
end
