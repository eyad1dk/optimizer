# Security and trust

V2.1 2.1.0 is an unsigned preview. SHA-256 checks integrity, not publisher identity. Manual update checks fetch bounded metadata only; no downloaded executable installation path exists.

Native setting changes use an allowlist, typed targets, saved intent, conditional writes and read-back. Recovery preserves conflicting external changes. No Defender, Firewall, security update or core security-service disabling is implemented. Profile imports contain validated IDs and values, never scripts.

Cleanup is intentionally irreversible and restricted to eight fixed cache roots/patterns. It checks local roots, excluded personal folders, reparse points, physical handle paths, file metadata and hardlinks; it denies concurrent writers/deleters while marking the exact opened file for deletion. Locked or changed files are skipped and results are rescanned. Never pass arbitrary paths across the elevated-helper interface.

System tools use absolute Windows executable paths and fixed arguments, without cmd.exe or PowerShell command interpolation. Named-pipe helpers restrict users and verify client process identity. Alternate-account elevation is not certified. Explorer restart verifies the current-session Windows shell executable and PID; it does not kill every process with a matching name.

The embedded PowerShell backend accepts structured data only; no user-provided scripts or interpolated command text. Recycle Bin, Delivery Optimization and servicing have separate explicit maintenance paths, not arbitrary-folder deletion.

Read docs/V2.1-IMPLEMENTATION.md for boundaries. Native Windows 11 and full elevation/mutation certification remain open. Report vulnerabilities privately to the repository owner; do not publish user recovery files or local command output containing personal data.
