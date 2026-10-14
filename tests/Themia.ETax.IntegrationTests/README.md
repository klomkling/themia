# Themia.ETax.IntegrationTests

Proves that Themia.ETax PDFs are byte-reproducible on Linux Chromium, the property the e-Tax "exactly one original"
rule rests on: two renders of the same stored snapshot, at different times and in fresh browsers, are identical once
the two PDF date fields are pinned.

Each case compares the SHA-256 of **normalised bytes from renders made in the same run**. There is deliberately no
committed golden PDF: the bytes are bound to the Chromium build, so a golden file would only test the build.

Run: `dotnet test tests/Themia.ETax.IntegrationTests` (needs network on first run for Chromium, ~150 MB).
CI: the nightly `integration.yml` picks it up through `Category=Integration`; before merging a change to
Themia.ETax or Themia.Pdf rendering, dispatch that workflow by hand on the branch.

**Which Chromium:** the build PuppeteerSharp downloads by default (pinned by the package version), the same one
`Themia.Pdf.IntegrationTests` uses. The test prints its version. An app that points `ThemiaPdfOptions.ExecutablePath`
at its own Docker Chromium runs a different build than CI proves; `renderer_version` records which one rendered and the
resend rule (`TaxDocumentEvidence.Check`) handles the drift.

On a mismatch the failure message names the browser version, the first differing offset (hex and ASCII) and a
classification, and both PDFs are written to `TestResults/etax-pdf-diff/` at the repo root (uploaded by CI).
