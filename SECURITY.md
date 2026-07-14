# Security policy

## Supported versions

The latest release is supported with security fixes.

## Threat model highlights

BitBroom deletes files as the invoking user (optionally elevated). The attack surfaces we
actively defend:

- **Link following (CWE-552):** junctions/symlinks/mount points are refused at scan,
  wildcard-expansion and delete time; cloud placeholders are refused via attribute checks.
  This class produced CVE-2025-3025 in a competing product; our regression tests include a
  junction canary.
- **Path traversal in rules:** rules are structural data validated against known bases;
  `..`, rooted patterns and env-var surprises are rejected (depth guard).
- **TOCTOU between scan and clean:** every deletion re-validates path containment and
  re-reads attributes immediately before the delete call.
- **Supply chain:** the cleaning engine and CLI have zero third-party runtime dependencies.
  The GUI's single dependency is the MIT-licensed WPF UI library (Fluent controls,
  pinned version, source-auditable, no network code); test projects use xunit.
- The only network code in the product is the GUI's optional update check: one HTTPS
  request to the GitHub releases API at startup (opt-out in Settings), and — only on an
  explicit "Install update" click — the installer download, which is verified against
  the release's SHA256SUMS.txt before execution. The engine and CLI make no network
  requests at all.

## Reporting a vulnerability

Please report privately via GitHub Security Advisories ("Report a vulnerability" on the
repository) or by email to **contact@bitbroom.app**, rather than public issues. Expect an
acknowledgement within 72 hours. Please include a proof-of-concept path layout if the
issue involves the deletion engine.
