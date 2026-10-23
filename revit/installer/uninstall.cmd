@echo off
del "%APPDATA%\Autodesk\Revit\Addins\2025\TeamHub.addin" 2>nul
del "%APPDATA%\Autodesk\Revit\Addins\2026\TeamHub.addin" 2>nul
rmdir /s /q "%APPDATA%\TeamHub\revit\bin" 2>nul
echo Team Hub removed from Revit.
pause
