# Contributing

Keep changes focused and describe the behavior a reviewer can reproduce. Use the .NET 10 build, simulated tests, native read-only smoke and layout checks in the README.

Every new mutation needs a stable versioned ID, official Windows documentation, typed originals, eligibility/policy checks, preview, intent-before-write storage, read-back verification and an honest rollback contract. Test stale state, policy changes, denied writes, cancellation, crashes and external conflicts. Optional registry operations must distinguish absent/present values and retain type/view/user scope before they can be accepted.

Default tests must never tune the developer's PC. Native writes, System Restore and real game-session exit scenarios require disposable Windows VMs with snapshots. Include the OS/build/privilege/hardware combination actually tested; do not expand the compatibility matrix by assumption.

UI changes need keyboard/focus/Narrator checks, minimum-size/1366×768 layouts and 100/150/200% scaling. Native capture is preferred; clearly identify built-in renderer output and deterministic fixtures. Fixtures must never appear as live hardware data.

Do not add security disabling, arbitrary tweak scripts, unreviewed destructive cleanup, blanket service changes, package removals in presets or unsupported performance promises. Keep dependencies small. Retain active journals across upgrades and preserve v0.1 regression fixtures.

Do not commit credentials, private reports, local history, bin/obj directories or executable build outputs. Use release assets for binaries. Read SECURITY.md before reporting a vulnerability.
