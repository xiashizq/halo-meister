-- Halo Meister: HaloScript dispatch probe, step 1 of 2.
--
-- Paste this into the Scripting page's Lua tab and run it while an offline campaign
-- mission is loaded. The Lua path is the only one that returns real values, so this
-- uses Lua to drive the console and a filesystem side effect to check the result.
--
-- Why this works: the HaloScript function script_doc writes hs_doc.txt. If any of the
-- candidate commands below reaches the HaloScript runtime, that file appears on disk.
-- If none of them do, the retail console has no HaloScript entry point and the
-- Scripting page's HaloScript tab cannot work as currently designed.
--
-- Step 1 (this file) records which candidate paths already exist, then submits every
-- candidate spelling. Step 2 (hs-dispatch-check.lua) reports what changed.

local UEHelpers = require("UEHelpers")

local report = {}
local function add(format, ...)
    report[#report + 1] = select("#", ...) == 0
        and format
        or string.format(format, ...)
end

local local_app_data = os.getenv("LOCALAPPDATA") or ""

-- Blam historically wrote its dumps next to the working directory; the UE wrapper may
-- redirect them under Saved. Check every plausible location.
local doc_paths = {
    "hs_doc.txt",
    "..\\hs_doc.txt",
    "reports\\hs_doc.txt",
    local_app_data .. "\\Meteorite\\Saved\\hs_doc.txt",
    local_app_data .. "\\Meteorite\\Saved\\Logs\\hs_doc.txt",
    local_app_data .. "\\Meteorite\\Saved\\BlamData\\hs_doc.txt",
}

local function stat(path)
    local file = io.open(path, "rb")
    if not file then return nil end
    local size = file:seek("end")
    file:close()
    return size
end

-- The working directory decides where the relative candidates above resolve to.
local ok_cwd, cwd = pcall(function()
    local pipe = io.popen("cd")
    if not pipe then return nil end
    local value = pipe:read("*l")
    pipe:close()
    return value
end)
add("Working directory: %s", (ok_cwd and cwd) or "unavailable")

add("")
add("Baseline hs_doc.txt state:")
for _, path in ipairs(doc_paths) do
    local size = stat(path)
    add("  %s  %s", size and (size .. " bytes") or "absent", path)
end

-- Both conventions the app currently mixes, plus the likely alternatives.
local candidates = {
    "script_doc",
    "script script_doc",
    "script (script_doc)",
    "hs script_doc",
    "hs (script_doc)",
    "blam script_doc",
}

local kismet = StaticFindObject("/Script/Engine.Default__KismetSystemLibrary")
local world = UEHelpers.GetWorld()
if not kismet or not kismet:IsValid() or not world or not world:IsValid() then
    add("")
    add("ABORTED: KismetSystemLibrary or the active World is unavailable.")
    add("Load an offline campaign mission and run this again.")
    return table.concat(report, "\n")
end

add("")
add("Submitted candidates:")
for index, command in ipairs(candidates) do
    local sent, err = pcall(function()
        kismet:ExecuteConsoleCommand(world, command, nil)
    end)
    add("  %d. %s -> %s", index, command, sent and "sent" or ("threw: " .. tostring(err)))
end

add("")
add("Now run hs-dispatch-check.lua. If a path gained an hs_doc.txt, HaloScript")
add("dispatch works and the candidate list above tells us the spelling to keep.")

return table.concat(report, "\n")
