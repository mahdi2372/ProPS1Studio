# Third-party notices

ProPS1 Studio incorporates or redistributes the following third-party components.

## ps2exe

- Project: https://github.com/MScholtes/PS2EXE
- Author: Markus Scholtes
- License: Microsoft Limited Public License 1.1 (MS-PL)
- Usage: PowerShell module invoked as a child process to compile `.ps1` scripts to `.exe`.
  Installed at runtime from the PowerShell Gallery; not bundled with the installer.
- Full text: `ps2exe-MS-PL.txt`

## UPX (Ultimate Packer for eXecutables)

- Project: https://github.com/upx/upx
- Author: The UPX team
- License: GNU General Public License version 2 or (at your option) any later version
- Usage: invoked as a separate command-line tool (`upx.exe`) to compress output executables.
  Downloaded at runtime from the official GitHub release (v5.2.1, win64), verified against a
  pinned SHA-256 hash. Not bundled with the installer.
- Full text: `upx-GPL-2.0.txt`

## .NET runtime

- Project: https://github.com/dotnet/runtime
- License: MIT License
- Usage: the self-contained release build embeds the .NET 8 desktop runtime.
- Full text: `dotnet-MIT.txt`
