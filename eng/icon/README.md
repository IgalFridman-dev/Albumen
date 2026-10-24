# Albumen app icon

`src/Albumen.App/Assets/Albumen.ico` is drawn by `New-AlbumenIcon.ps1`; no third-party asset
is involved.

- **The mark:** the white stacked-photo mark of the logo tile in `MainWindow.xaml` (a photo,
  the edge of a second one behind it, and a hill line), on a rounded square (corner radius 22 %
  of the edge) filled with the blue-to-violet gradient `#3B82F6` → `#8B5CF6`, top left to bottom
  right.
- **Sizes:** 16, 20, 24, 32, 48, 64, 128 and 256 px, each stored as a PNG inside one `.ico`.
- **Where it is used:** `ApplicationIcon` in `Albumen.App.csproj` embeds it in the exe
  (Explorer and the taskbar); `MainWindow` and `SurvivorOverrideDialog` load it as a resource for
  their title bar.

## Regenerate

Windows PowerShell 5.1 (GDI+ through `System.Drawing`, no package):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File eng\icon\New-AlbumenIcon.ps1
```

The output depends only on the script, so a second run reproduces the committed file byte for
byte; `tests/tooling/AlbumenIcon.Tests.ps1` checks that and the eight PNG entries.
