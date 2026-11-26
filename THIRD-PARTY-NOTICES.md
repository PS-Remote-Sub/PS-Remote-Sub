# Third-party notices

## NoSleep.js 0.12.0

Vendored as `src/PSRemoteSub.Web/wwwroot/nosleep.min.js` (from https://unpkg.com/nosleep.js@0.12.0/dist/NoSleep.min.js).
Keeps phone and tablet screens awake on the subtitle page. Includes two embedded base64 video data URIs used as the fallback.

MIT License

Copyright (c) Rich Tibbett

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## NuGet packages (all MIT; license verified from each package's `.nuspec`)

| Package | Version | Copyright |
|---|---|---|
| Avalonia (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent) | 12.1.6 | Copyright 2013-2026 The AvaloniaUI Project |
| QRCoder | 1.9.0 | Copyright 2013-2025 Raffael Herrmann; Copyright 2024-2025 Shane Krueger |
| Velopack | 1.2.161 | Copyright Velopack Ltd. |
| Vortice.Windows (Vortice.Direct3D11, Vortice.DXGI, Vortice.MediaFoundation and their dependencies) | 3.8.3 | Copyright (c) Amer Koleci and Contributors |

Each of these is licensed under the MIT License: https://licenses.nuget.org/MIT (the MIT text reproduced above applies, with the respective copyright holder).


## Transitive packages and native components shipped in the app

Full license texts that are not reproduced in this file are shipped **verbatim** (copied byte for byte from the NuGet
packages) in the [`licenses/`](licenses/) folder, which is published next to the app.

| Component | Version | License | Full text |
|---|---|---|---|
| Avalonia.Skia, Avalonia.HarfBuzz (Avalonia dependencies) | 12.1.6 | MIT, Copyright 2013-2026 The AvaloniaUI Project (from `.nuspec`) | MIT text above |
| SkiaSharp, SkiaSharp.NativeAssets.Win32 | 3.119.4 | MIT, Copyright (c) 2015-2016 Xamarin, Inc.; (c) 2017-2018 Microsoft Corporation | [`licenses/SkiaSharp-HarfBuzzSharp-LICENSE.txt`](licenses/SkiaSharp-HarfBuzzSharp-LICENSE.txt) |
| HarfBuzzSharp, HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.3 | MIT, same copyright and identical text as SkiaSharp | [`licenses/SkiaSharp-HarfBuzzSharp-LICENSE.txt`](licenses/SkiaSharp-HarfBuzzSharp-LICENSE.txt) |
| Third-party code bundled in the native `libSkiaSharp.dll` / `libHarfBuzzSharp.dll` (see below) | | various | [`licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt`](licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt) |
| Avalonia.Angle.Windows.Natives (ANGLE, `av_libglesv2.dll`) | 2.1.27548.20260419 | BSD-3-Clause, Copyright 2018 The ANGLE Project Authors | [`licenses/Avalonia.Angle.Windows.Natives-LICENSE.txt`](licenses/Avalonia.Angle.Windows.Natives-LICENSE.txt) |
| QRCoder | 1.9.0 | MIT, Copyright (c) 2013-2025 Raffael Herrmann; (c) 2024-2025 Shane Krueger | [`licenses/QRCoder-LICENSE.txt`](licenses/QRCoder-LICENSE.txt) |
| Vortice.DirectX, Vortice.Mathematics (Vortice dependencies) | 3.8.3 / 2.1.0 | MIT, Copyright (c) Amer Koleci and Contributors (from `.nuspec`) | MIT text above |
| SharpGen.Runtime, SharpGen.Runtime.COM (Vortice dependencies) | 2.4.2-beta | MIT (from `.nuspec`) | MIT text above |
| .NET runtime (Microsoft.NETCore.App.Runtime.win-x64, included in the self-contained build) | 10.0.15 | MIT, Copyright (c) .NET Foundation and Contributors | [`licenses/dotnet-LICENSE.txt`](licenses/dotnet-LICENSE.txt); its own notices: [`licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt`](licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt) |
| ASP.NET Core shared framework (Microsoft.AspNetCore.App.Runtime.win-x64, used by the LAN web page) | 10.0.15 | MIT, Copyright (c) .NET Foundation and Contributors (same text as the .NET runtime) | [`licenses/dotnet-LICENSE.txt`](licenses/dotnet-LICENSE.txt); its own notices: [`licenses/aspnetcore-THIRD-PARTY-NOTICES.txt`](licenses/aspnetcore-THIRD-PARTY-NOTICES.txt) |

### Libraries bundled inside the SkiaSharp / HarfBuzzSharp native binaries

`SkiaSharp.NativeAssets.Win32` and `HarfBuzzSharp.NativeAssets.Win32` ship one identical `THIRD-PARTY-NOTICES.txt`
(copied verbatim to [`licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt`](licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt)).
It holds the original copyright notices and full license texts for, among others:

| Library | License (as given in that file) |
|---|---|
| Skia | BSD-3-Clause (Google Inc.) |
| HarfBuzz | "Old MIT" |
| ANGLE | BSD-3-Clause |
| libpng | libpng license |
| zlib | zlib license |
| FreeType | dual-licensed: FreeType License (FTL) or GPLv2 |
| expat | MIT |
| libjpeg-turbo | IJG License, Modified (3-clause) BSD License and zlib License |
| libwebp | BSD-3-Clause |
| ICU, etc1, gif, DNG SDK, piex, sfntly, SPIR-V Headers/Tools, jsoncpp, imgui, libmicrohttpd, sdl | see the file |

The file is authoritative; the table above is only an index into it.

FreeType credit, as its license asks: Portions of this software are copyright © The FreeType Project
(www.freetype.org). All rights reserved. (The FreeType version bundled in SkiaSharp 3.119.4 is not stated in the
package, so no year is given.)

## Android SDK Platform-Tools (adb)

Windows build: `tv/adb/win/adb.exe`, `AdbWinApi.dll` and `AdbWinUsbApi.dll` are shipped unmodified from Android SDK
Platform-Tools 37.0.3 and used to install and talk to the TV app. adb is part of the Android Open Source Project,
licensed under the Apache License 2.0, Copyright The Android Open Source Project. The full NOTICE file of that package
(Apache-2.0 text and the notices of the components it lists) is shipped verbatim in
[`licenses/android-platform-tools-NOTICE.txt`](licenses/android-platform-tools-NOTICE.txt).

macOS build: `tv/adb/mac/adb` (universal x86_64 + arm64) comes from Android SDK Platform-Tools 37.0.3
(`Pkg.Revision=37.0.3`, `adb version` 1.0.41, 37.0.3-16887417), downloaded from Google's
`platform-tools-latest-darwin.zip`, where it is signed Developer ID Application: Google LLC. Its code is not changed,
but its signature is: Google's signature is replaced by an ad-hoc one when the binary is sealed into the app bundle
(`tools/mac/make-app.sh`, and again by `vpk pack` for releases). Same license; the NOTICE file of that macOS package is
shipped verbatim in
[`licenses/android-platform-tools-darwin-NOTICE.txt`](licenses/android-platform-tools-darwin-NOTICE.txt).

Each build ships only its own adb and NOTICE file.

## IBM Plex Sans Thai 1.1

`src/PSRemoteSub.App/Assets/Fonts/IBMPlexSansThai-{Regular,Medium,SemiBold,Bold}.ttf` are shipped unmodified from
https://github.com/google/fonts/tree/main/ofl/ibmplexsansthai and used as the app's UI font.
Copyright © 2017 IBM Corp. with Reserved Font Name "Plex". Licensed under the SIL Open Font License, Version 1.1; the
full license text is shipped in [`licenses/IBM-Plex-Sans-Thai-OFL.txt`](licenses/IBM-Plex-Sans-Thai-OFL.txt).
