# PhotoSift V1 third-party notices

This self-contained win-x64 package contains the components below, including
the .NET runtime and the WPF part of the Windows Desktop runtime, so no
separate .NET install is needed. The original inventory (host and SQLite
components) was owner-approved on 2026-09-23, and the complete inventory with
the bundled runtime was owner-approved on 2026-10-17.

| Component | Version | Shipped file(s) | Terms and packaged notices |
| --- | --- | --- | --- |
| .NET Host | 10.0.11 | `PhotoSift.App.exe` | [LicenseRef-Microsoft-NET-Library](https://www.microsoft.com/net/dotnet_library_license.htm); `licenses/dotnet/LICENSE.txt`, `licenses/dotnet/ThirdPartyNotices.txt` |
| .NET Runtime | 10.0.11 | 187 files from `Microsoft.NETCore.App.Runtime.win-x64` (for example `coreclr.dll`, `System.Private.CoreLib.dll`) | [MIT](https://licenses.nuget.org/MIT); `licenses/dotnet-runtime/LICENSE.txt`, `licenses/dotnet-runtime/THIRD-PARTY-NOTICES.txt` |
| Microsoft.Data.Sqlite | 10.0.11 | Dependency metapackage; no separate file | [MIT](https://github.com/dotnet/dotnet/blob/v10.0.11/LICENSE.TXT); `licenses/MIT.txt` |
| Microsoft.Data.Sqlite.Core | 10.0.11 | `Microsoft.Data.Sqlite.dll` | [MIT](https://github.com/dotnet/dotnet/blob/v10.0.11/LICENSE.TXT); `licenses/MIT.txt` |
| Native SQLite | 3.53.3 | `e_sqlite3.dll` | [blessing](https://sqlite.org/copyright.html); `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | `SQLitePCLRaw.batteries_v2.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.core | 2.1.12 | `SQLitePCLRaw.core.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.12 | `e_sqlite3.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.12 | `SQLitePCLRaw.provider.e_sqlite3.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| Windows Desktop / WPF | 10.0.11 | 209 WPF-profile files from `Microsoft.WindowsDesktop.App.Runtime.win-x64` (for example `PresentationFramework.dll`, `wpfgfx_cor3.dll`, localized resources) | [MIT](https://licenses.nuget.org/MIT); `licenses/dotnet-windowsdesktop/LICENSE.txt` |

The .NET apphost comes from the pinned SDK 10.0.400 host pack 10.0.11.
Its captured Windows binary license is the Microsoft .NET Library License.
Both `licenses/dotnet/LICENSE.txt` and `licenses/dotnet/ThirdPartyNotices.txt`
are retained verbatim from that SDK. The .NET source license does not replace
these captured binary terms.

Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core are from
[dotnet/dotnet](https://github.com/dotnet/dotnet/tree/v10.0.11).
The four SQLitePCLRaw packages are from
[SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.12).
Its complete upstream notice is retained, including the SQLite blessing and
Microsoft Open Technologies attribution. It also discusses upstream variants;
this release's inventory contains no SQLCipher or OpenSSL payload.

Native [SQLite](https://sqlite.org/copyright.html) is dedicated to the public
domain. The captured native version is 3.53.3; its SHA-256 is
`B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E`.

The .NET Runtime and Windows Desktop runtime files are copied unchanged from
the pinned NuGet runtime packs (10.0.11). Their `LICENSE` and
`THIRD-PARTY-NOTICES` files are retained verbatim from those packs; the
Windows Desktop pack ships no separate third-party notice. Only the WPF
profile of the Windows Desktop runtime is included, not Windows Forms.
SDK tools and build-only packs are not shipped.

The exact file list for every component is in the versioned machine-readable
inventory `eng/release/release-dependencies.json` in the PhotoSift source
repository. All entries have disposition `owner-approved`.
