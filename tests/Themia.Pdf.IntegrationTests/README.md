# Themia.Pdf.IntegrationTests

These tests launch headless Chromium via PuppeteerSharp. On first run they download Chromium
(~150 MB) to the PuppeteerSharp cache directory; subsequent runs reuse it. They require:

- Network access on first run (or a pre-provisioned Chromium / a `ThemiaPdfOptions.ExecutablePath`).
- A Linux runner needs the usual Chromium shared libraries.

Run only this suite: `dotnet test tests/Themia.Pdf.IntegrationTests`.
Filter by trait: `dotnet test --filter Category=Integration`.
