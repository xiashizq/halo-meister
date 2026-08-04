@echo off
setlocal
set "VSDEVCMD=C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat"
if not exist "%VSDEVCMD%" set "VSDEVCMD=C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat"
if not exist "%VSDEVCMD%" set "VSDEVCMD=C:\Program Files\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"
if not exist "%VSDEVCMD%" set "VSDEVCMD=C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\Common7\Tools\VsDevCmd.bat"
if not exist "%VSDEVCMD%" (
  echo Visual Studio C++ build tools were not found.
  exit /b 1
)
call "%VSDEVCMD%" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b %errorlevel%
if not exist "..\..\src\HaloMeister.App\Assets\UE4SS" mkdir "..\..\src\HaloMeister.App\Assets\UE4SS"
if not exist "obj" mkdir "obj"
cl /nologo /std:c++17 /O2 /GL /EHsc /MT /W4 /WX /utf-8 /LD /DUNICODE /D_UNICODE ^
  /Fo:"obj\blam_bridge.obj" ^
  blam_bridge.cpp /link /LTCG /OPT:REF /OPT:ICF ^
  /IMPLIB:"obj\blam_bridge.lib" ^
  /OUT:"..\..\src\HaloMeister.App\Assets\UE4SS\halomeister_blam_v45.dll"
exit /b %errorlevel%
