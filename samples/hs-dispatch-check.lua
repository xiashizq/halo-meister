-- Halo Meister: HaloScript dispatch probe, step 2 of 2.
--
-- Run hs-dispatch-probe.lua first, then paste this into the Scripting page's Lua tab.
-- It reports whether any candidate console command produced hs_doc.txt, and dumps the
-- head of that file if it did.
--
-- An hs_doc.txt that exists proves the HaloScript runtime is reachable from the console
-- and gives us the real function signatures for drop / object_create, which is what a
-- working per-weapon "give" needs. All-absent means the retail console has no HaloScript
-- entry point and the HaloScript tab needs a different approach entirely.

local report = {}
local function add(format, ...)
    report[#report + 1] = select("#", ...) == 0
        and format
        or string.format(format, ...)
end

local local_app_data = os.getenv("LOCALAPPDATA") or ""

local doc_paths = {
    "hs_doc.txt",
    "..\\hs_doc.txt",
    "reports\\hs_doc.txt",
    local_app_data .. "\\Meteorite\\Saved\\hs_doc.txt",
    local_app_data .. "\\Meteorite\\Saved\\Logs\\hs_doc.txt",
    local_app_data .. "\\Meteorite\\Saved\\BlamData\\hs_doc.txt",
}

local found = nil
add("hs_doc.txt state:")
for _, path in ipairs(doc_paths) do
    local file = io.open(path, "rb")
    if file then
        local size = file:seek("end")
        file:close()
        add("  %d bytes  %s", size, path)
        if not found and size > 0 then found = path end
    else
        add("  absent    %s", path)
    end
end

if not found then
    add("")
    add("No hs_doc.txt anywhere: none of the candidate spellings reached the")
    add("HaloScript runtime. Treat console-based HaloScript as unsupported.")
    return table.concat(report, "\n")
end

add("")
add("FOUND: %s", found)
add("First 40 lines:")

local file = io.open(found, "rb")
local shown = 0
for line in file:lines() do
    add("  %s", line)
    shown = shown + 1
    if shown >= 40 then break end
end
file:close()

add("")
add("Copy the full file out of that path - it lists every HaloScript function")
add("and its argument types, including drop and object_create.")

return table.concat(report, "\n")
