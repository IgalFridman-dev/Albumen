# Albumen V1 third-party notices

This self-contained win-x64 package contains the components below, including
the .NET runtime and the WPF part of the Windows Desktop runtime, so no
separate .NET install is needed, and the SigLIP 2 model with ONNX Runtime for
look-alike grouping and text search. The original inventory (host and SQLite
components) was owner-approved on 2026-09-23 and the inventory with the bundled
runtime on 2026-10-17. The 1.1 additions (ONNX Runtime, the tokenizer and its
dependencies, the SigLIP 2 model) await owner approval.

| Component | Version | Shipped file(s) | Terms and packaged notices |
| --- | --- | --- | --- |
| .NET Host | 10.0.11 | `Albumen.App.exe` | [LicenseRef-Microsoft-NET-Library](https://www.microsoft.com/net/dotnet_library_license.htm); `licenses/dotnet/LICENSE.txt`, `licenses/dotnet/ThirdPartyNotices.txt` |
| .NET Runtime | 10.0.11 | 187 files from `Microsoft.NETCore.App.Runtime.win-x64` (for example `coreclr.dll`, `System.Private.CoreLib.dll`) | [MIT](https://licenses.nuget.org/MIT); `licenses/dotnet-runtime/LICENSE.txt`, `licenses/dotnet-runtime/THIRD-PARTY-NOTICES.txt` |
| Google.Protobuf | 3.30.2 | `Google.Protobuf.dll` | [BSD-3-Clause](https://licenses.nuget.org/BSD-3-Clause); `licenses/protobuf/LICENSE.txt` |
| Microsoft.Data.Sqlite | 10.0.11 | Dependency metapackage; no separate file | [MIT](https://github.com/dotnet/dotnet/blob/v10.0.11/LICENSE.TXT); `licenses/MIT.txt` |
| Microsoft.Data.Sqlite.Core | 10.0.11 | `Microsoft.Data.Sqlite.dll` | [MIT](https://github.com/dotnet/dotnet/blob/v10.0.11/LICENSE.TXT); `licenses/MIT.txt` |
| Microsoft.ML.OnnxRuntime | 1.31.0 | `onnxruntime.dll`, `onnxruntime_providers_shared.dll` | [MIT](https://licenses.nuget.org/MIT); `licenses/onnxruntime/LICENSE.txt`, `licenses/onnxruntime/ThirdPartyNotices.txt` |
| Microsoft.ML.OnnxRuntime.Managed | 1.31.0 | `Microsoft.ML.OnnxRuntime.dll` | [MIT](https://licenses.nuget.org/MIT); `licenses/onnxruntime/LICENSE.txt`, `licenses/onnxruntime/ThirdPartyNotices.txt` |
| Microsoft.ML.Tokenizers | 2.0.1 | `Microsoft.ML.Tokenizers.dll` | [MIT](https://licenses.nuget.org/MIT); `licenses/ml-tokenizers/LICENSE.txt`, `licenses/ml-tokenizers/THIRD-PARTY-NOTICES.txt` |
| Native SQLite | 3.53.3 | `e_sqlite3.dll` | [blessing](https://sqlite.org/copyright.html); `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | `SQLitePCLRaw.batteries_v2.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.core | 2.1.12 | `SQLitePCLRaw.core.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.12 | `e_sqlite3.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.12 | `SQLitePCLRaw.provider.e_sqlite3.dll` | [Apache-2.0](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT); `licenses/SQLitePCLRaw-LICENSE.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` |
| SigLIP 2 base patch16-224 (ONNX export) | 1 | `models/vision.onnx`, `models/text.onnx`, `models/tokenizer.model` | [Apache-2.0](https://www.apache.org/licenses/LICENSE-2.0); `licenses/siglip2/LICENSE.txt` |
| System.Numerics.Tensors | 9.0.0 | `System.Numerics.Tensors.dll` | [MIT](https://licenses.nuget.org/MIT); `licenses/system-numerics-tensors/LICENSE.txt` |
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

The model files are an ONNX export of Google's
[SigLIP 2](https://huggingface.co/google/siglip2-base-patch16-224) (Apache-2.0)
made by `eng/models/export_siglip2.py`: the vision and text towers in fp32, the
text tower's vocabulary table stored in 16 bits, and the unchanged
SentencePiece `tokenizer.model`. Their SHA-256 values are in
`models/manifest.json`. ONNX Runtime and Microsoft.ML.Tokenizers are from
[onnxruntime](https://github.com/microsoft/onnxruntime/tree/v1.31.0) and
[dotnet/machinelearning](https://github.com/dotnet/machinelearning); their
license and third-party notice files are retained verbatim from the packages.
Google.Protobuf ships no license file; its BSD-3-Clause text is copied from
[protobuf v30.2](https://github.com/protocolbuffers/protobuf/blob/v30.2/LICENSE).

The exact file list for every component is in the versioned machine-readable
inventory `eng/release/release-dependencies.json` in the Albumen source
repository. All entries have disposition `pending-owner-approval` until the
1.1 additions are approved.
