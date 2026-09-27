# Private portable packages

`scripts/Start-Coop.ps1` and `Start-Coop.cmd` can be copied into a portable LiveSplit root. Include the user's selected splits/layouts, required components and licenses, and the built Coop DLL. Each layout must contain one `LiveSplit.Coop.dll` component. Preserve the user's originals; create separate host and viewer directories. Do not distribute test drivers or SSH/TLS keys.

`Paket.json` describes the choices:

```json
{"profiles":[{"id":"duo","title":"Map - Duo","run":"duo.lss","layout":"coop.lsl"}]}
```

`Zugang.private.json` supplies only that package's role-specific key:

```json
{"address":"wss://your-domain:8443/coop","room":"coop","role":"host","key":"REPLACE_WITH_THE_ROOM_HOST_KEY"}
```

For viewers use `role: viewer` and the view key. Remove all autosplitter components from viewer layouts, disable registered autosplitters and local timer hotkeys. A placeholder game name that does not match a registered autosplitter is useful. The viewer adopts the host's run in memory after connecting.

The launcher prompts for a profile, resolves ASL filenames inside `Components`, updates the selected run's layout path, and encrypts the access key with DPAPI for the current Windows user. Then it launches LiveSplit from the package directory. The user clicks **Coop: Connect**. Connections are not automatic. On a new PC or after moving the folder, launch through the CMD again; do not copy DPAPI ciphertext alone.

The raw role key intentionally remains in the private JSON so a recipient can initialize the package. Share these archives privately, never as public releases. Public releases must omit that file and instruct recipients to use their own server/key. Keep generated private packages under a gitignored directory. The host saves authoritative results with **Save Splits**; viewer packages do not automatically archive host history.

For a packaging check without starting LiveSplit:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Start-Coop.ps1 -Profile duo -PrepareOnly
```

The prepared package itself should ship with portable relative paths and empty `ProtectedKey` values, so the recipient's first launch performs initialization. Test moved copies, not the handoff folder, to preserve this state.
