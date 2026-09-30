# PhotoSift V1 third-party notices

This framework-dependent win-x64 package contains the components below.
Product-owner redistribution approval for this exact component inventory was
given on 2026-09-23. Release verification remains pending; this notice is not
a claim that E5-S4 is complete.

| Component | Version | Shipped file(s) | Terms and packaged notices |
| --- | --- | --- | --- |
| .NET Host | 10.0.11 | `PhotoSift.App.exe` | [LicenseRef-Microsoft-NET-Library](https://www.microsoft.com/net/dotnet_library_license.htm); `licenses/dotnet/LICENSE.txt`, `licenses/dotnet/ThirdPartyNotices.txt` |
| Microsoft.Data.Sqlite | 10.0.11 | Dependency metapackage; no separate file | [MIT](https://github.com/dotnet/dotnet/blob/v10.0.11/LICENSE.TXT); `licenses/MIT.txt` |
| Microsoft.Data.Sqlite.Core | 10.0.11 | `Microsoft.Data.Sqlite.dll` | [MIT](https://github.com/dotnet/dotnet/blob/v10.0.11/LICENSE.TXT); `licenses/MIT.txt` |
| Native SQLite | 3.53.3 | `e_sqlite3.dll` | [blessing](https://sqlite.org/copyright.html); `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | `SQLitePCLRaw.batteries_v2.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.core | 2.1.12 | `SQLitePCLRaw.core.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.12 | `e_sqlite3.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.12 | `SQLitePCLRaw.provider.e_sqlite3.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |

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

The separately installed x64 .NET Desktop Runtime is a prerequisite and is not
included in this package. SDK tools and build-only runtime packs are not shipped.
The versioned machine-readable inventory is `eng/release/release-dependencies.json`
in the PhotoSift source repository. All entries have disposition `owner-approved`.
