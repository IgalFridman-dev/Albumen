# Hyper-V testing ground

The verification VM `PhotoSift-Verification` keeps the app's former name, as do its guest account and
`C:\PhotoSift` folder, so the existing checkpoints stay valid after the rename to Albumen.

The verification VM `PhotoSift-Verification` is a Windows 11 Enterprise
Evaluation Generation 2 guest with **no virtual network adapter**. The host
drives it only through PowerShell Direct, and every script refuses to run if
a network adapter is attached. All scripts need an elevated PowerShell.

| Checkpoint | Contents |
|---|---|
| `Clean-Evaluation-Base` | Fresh unattended install. Immutable recovery point. |
| `Credential-Evaluation-Base` | Guest account `PHOTOSIFTVM\PhotoSiftRunner`. |
| `Toolchain-Evaluation-Base` | SDK 10.0.400, Desktop runtime and offline package cache under `C:\PhotoSift\golden`. Used by every run. |

The guest password is never stored in Git. Scripts prompt for it, or accept
`-Credential`.

## Restore the VM on a new host

Copy the VM backup folder (the one with `Configuration\` and `Disk\`) to a
working location first. Hyper-V takes ownership of whatever it registers.

```powershell
.\eng\verification\Import-AlbumenVerificationVm.ps1 -VmRoot E:\Hyper-V\PhotoSift-Verification
```

The import repoints disk paths recorded for the old drive. A vTPM is bound to
the Windows installation that created it. After a host reinstall, starting
the VM fails with "The key protector could not be unwrapped". In that case,
rebuild it with a fresh vTPM. The rebuild keeps all checkpoints and names:

```powershell
.\eng\verification\Repair-AlbumenVerificationVmTpm.ps1 -NewVmRoot E:\Hyper-V\PhotoSift-Verification-v2
```

## Run the app in the VM (UI smoke)

```powershell
.\eng\verification\Invoke-AlbumenUiSmoke.ps1
```

The script does the following:

1. Publishes `src\Albumen.App` on the host as framework-dependent `win-x64`.
   Pass `-PackagePath` to test an existing build.
2. Restores `Toolchain-Evaluation-Base` and boots the VM.
3. Turns on autologon for this run only, then restarts the guest.
4. Copies the package in and launches it on the guest desktop.
5. Collects results into `artifacts\ui-smoke\<run>\`:
   - `screen.png`
   - `uia-tree.json`: every UI Automation element and its text.
   - `app-state.json`
   - the app's `%LOCALAPPDATA%\Albumen` data
   - application event-log warnings and errors
   - `summary.json`
6. Turns the VM off and restores the checkpoint again, which also discards
   the autologon password.

Use `-KeepRunning` to leave the VM at the desktop for hands-on inspection.
`-Launches 2` closes the main window normally between launches and records the
local data files after each close. `-AppTheme Dark` switches the guest user to
dark app mode before launching.

## Run the test suite

On the host, `dotnet test Albumen.slnx` runs everything except the tests
that touch real files, links and the Recycle Bin. Those 95 tests refuse to
run outside a disposable VM run. Another 8 need privileges a normal user
lacks. The full suite runs in the VM:

```powershell
.\eng\verification\Invoke-AlbumenHyperV.ps1 -Stage Full
```
