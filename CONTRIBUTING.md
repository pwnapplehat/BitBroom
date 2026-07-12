# Contributing to BitBroom

Thanks for helping make Windows cleaning boring and safe.

## Ground rules

1. **Safety is the product.** Anything that touches deletion goes through `PathGuard`,
   the walker and `SafeDeleter` — no new deletion code paths. Read
   [docs/SAFETY.md](docs/SAFETY.md) before touching the engine.
2. **Evidence or it doesn't ship.** New categories need sources (vendor/Microsoft docs or
   strong community evidence) added to [docs/RESEARCH.md](docs/RESEARCH.md) and an entry in
   [docs/CATEGORIES.md](docs/CATEGORIES.md).
3. **The refusal list is load-bearing.** Registry cleaning, prefetch purging and friends
   (see CATEGORIES.md → "Deliberately excluded") will not be accepted, with any UI.
4. **Tests must stay green**: `dotnet test tests\BitBroom.Core.Tests`. If you add engine
   behaviour, add a test. If you add a category, `CatalogSanityTests` already gates it —
   make sure it passes on a real machine, not just in theory.

## Adding a cleaning category (checklist)

- [ ] Declarative rules in `src/BitBroom.Core/Catalog/CategoryCatalog.cs`
- [ ] Correct `Risk` (only `Safe` may be `EnabledByDefault` — enforced by a test)
- [ ] `RequiresAdmin` set honestly
- [ ] Exclusions encoded structurally (e.g. Spotify cleans `LocalCache`, never `LocalState\Spotify\Storage`)
- [ ] Sources in RESEARCH.md, row in CATEGORIES.md
- [ ] `dotnet test` green

## Code style

- C# latest, nullable enabled, file-scoped namespaces (`.editorconfig` is authoritative).
- No new NuGet dependencies without discussion — the zero-dependency UI and small
  supply-chain surface are deliberate.
- Comments explain *why*, not *what*.

## Reporting bugs

Include: Windows version, BitBroom version, whether elevated, the category involved, and
the relevant audit log from `%LocalAppData%\BitBroom\logs` (paths in logs are local to
your machine — redact anything you consider private).

## Security issues

See [SECURITY.md](SECURITY.md) — please do not open public issues for vulnerabilities.
