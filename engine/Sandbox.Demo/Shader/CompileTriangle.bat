@echo off
setlocal

set "SHADER_DIR=%~dp0"
set "CORE_SHADERS=%~dp0..\..\..\game\core\shaders"

where slangc >nul 2>&1
if errorlevel 1 (
	echo Error: slangc was not found on PATH. 1>&2
	exit /b 1
)

slangc -enable-effect-annotations -target spirv -stage vertex -entry MainVs -I "%CORE_SHADERS%" "%SHADER_DIR%sdl_triangle.VS.slang" -o "%SHADER_DIR%sdl_triangle.VS.spv"
if not "%errorlevel%"=="0" exit /b 1

slangc -enable-effect-annotations -target spirv -stage fragment -entry MainPs -I "%CORE_SHADERS%" "%SHADER_DIR%sdl_triangle.PS.slang" -o "%SHADER_DIR%sdl_triangle.PS.spv"
if not "%errorlevel%"=="0" exit /b 1

echo Compiled both shaders successfully.
exit /b 0
