Nook for Windows (VERSION)
==========================

Run
---
1. Unzip this folder anywhere (for example Documents\Nook). Don't run it from inside the zip.
2. Double-click Nook.exe. Windows 10 or 11 (64-bit) is needed; nothing else to install.

The first start takes a few seconds longer: Nook unpacks its graphics libraries into
%TEMP%\.net once.

"Windows protected your PC"
---------------------------
Nook isn't code-signed yet, so SmartScreen may warn the first time. Click "More info",
then "Run anyway". (If the button is missing: right-click Nook.exe > Properties, tick
"Unblock", OK.)

Your key and settings
---------------------
Stored in %APPDATA%\Nook (paste that into the Explorer address bar):
- keys.dat       your saved login key, encrypted with Windows DPAPI for your Windows
                 account only (other accounts and other PCs can't read it)
- settings.json  theme, backend override, last user and last coding folder
Log out in Nook, or delete the folder, to remove them.

Coding mode
-----------
Commands the model asks to run go through PowerShell (PowerShell 7 "pwsh" if it is
installed, otherwise the built-in Windows PowerShell), inside the folder you picked,
and only after you click Approve.
