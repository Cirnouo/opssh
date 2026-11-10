# Third-party notices

opssh's first-party source is licensed under [Apache-2.0](LICENSE). The Windows distributions include third-party code and runtime files under their own terms. The pinned versions and accompanying notice files are listed in the [distribution manifest](licenses/manifest.json).

- The Windows Go core includes the Go standard library and the modules selected by the production Windows build, including the 1Password Go SDK, its embedded core and its Go dependencies. The individual upstream licenses and notices are under [licenses/go](licenses/go/). Use of 1Password services has separate [API and SDK terms](https://1password.com/legal/api-sdk-terms-of-service).
- The self-contained tray includes the .NET runtime, the Windows App SDK and the Windows SDK and WebView2 projections. Their pinned package notices are under [licenses/nuget](licenses/nuget/). The .NET `Microsoft.NETCore.App.Host.win-x64` 10.0.14 apphost has the same license and notice bytes as the pinned x64 .NET runtime package. The Windows SDK reference package points to the Microsoft SDK license provided there.
- The installers use WiX 7.0.1. The MSI uses an unmodified utility custom action and the EXE uses the unmodified Burn engine and standard bootstrapper. WiX code is provided under its [Microsoft Reciprocal License](licenses/wix/LICENSE.TXT). The corresponding [WiX 7.0.1 source archive](https://github.com/wixtoolset/wix/archive/642f1a0f7c857ffa4fc9fa3f9d1433aab895dd29.zip) includes the installer code. The package legal payload includes the same pinned source archive.

Before shipping a change to a toolchain or dependency, compare the actual Windows build and installer inputs with the manifest. Update the applicable version entries and upstream notice files together. Preserve upstream wording and source bytes.

The Windows App SDK and Windows SDK package terms also have distribution requirements for downstream distributors and end users. Before a public release, review how those terms apply to each of the MSI, EXE and ZIP. See the [pinned Windows App SDK license](licenses/nuget/microsoft.windowsappsdk/license.txt) and the Windows SDK terms in the NuGet notice inventory.

The 1Password Go SDK v0.4.1 embeds WASM core build `0040102`. The SDK license and the identified ICU normalizer notice are in the SDK's notice directory. The upstream Go release does not publish a complete attribution inventory for that exact WASM. Before the first release, obtain the matching upstream notice inventory or document a reviewed basis for redistribution and attribution. Record that evidence against the WASM digest in the notice manifest. [Pinned Go SDK release](https://github.com/1Password/onepassword-sdk-go/releases/tag/v0.4.1) · [Embedded WASM](https://github.com/1Password/onepassword-sdk-go/blob/f60aea196b471a9c35ddad5f9096e5fd2b5c939c/internal/wasm/core.wasm)

## GitHub mark

The tray uses the [GitHub mark from Octicons 19.21.2](https://github.com/primer/octicons/blob/v19.21.2/icons/mark-github-16.svg) under the MIT License.

```text
MIT License

Copyright (c) 2026 GitHub Inc.

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
```

## WSL Settings navigation shell

The tray adapts the [WSL Settings ShellPage](https://github.com/microsoft/WSL/blob/8585e23a45261be22e642562a315937a9562f1e8/src/windows/wslsettings/Views/Settings/ShellPage.xaml) and its [pane toggle behavior](https://github.com/microsoft/WSL/blob/8585e23a45261be22e642562a315937a9562f1e8/src/windows/wslsettings/Views/Settings/ShellPage.xaml.cs) under the [MIT License](https://github.com/microsoft/WSL/blob/8585e23a45261be22e642562a315937a9562f1e8/LICENSE).

```text
MIT License

Copyright (c) Microsoft Corporation.

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
SOFTWARE
```
