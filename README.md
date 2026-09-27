[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/build-and-test.yml?style=for-the-badge)](https://github.com/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/daily-automatic-update.yml?style=for-the-badge&label=Daily%20Update)](https://github.com/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/actions/workflows/daily-automatic-update.yml)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.LinkedIn.Runners.OpenApiClient
### A runner that regenerates and updates Soenneker.LinkedIn.OpenApiClient.

This runner executes a GitHub action that updates another project. It's not meant for consumption.

The runner uses Playwright Chromium to discover and read official LinkedIn documentation on Microsoft Learn. Shared parsing rules extract schema tables, linked types, enums, HTTP operations and JSON examples; there are no per-page field mappings. The generated OpenAPI retains source URLs and marks schemas inferred from examples. Unresolved definitions are recorded in `documentation/coverage.json` and prevent publication by default.

Each run saves normalized page snapshots, content hashes and a specification change report. The daily workflow caches this baseline independently of client publication, uploads review artifacts, and runs the existing OpenAPI fixer, Kiota and build/push steps when the published specification changes. Previously readable pages or generated operations cannot silently disappear from the baseline.

The runner uses `Soenneker.Playwrights.Installation` to ensure Chromium and its dependencies are installed before launching the browser. For a local preview, build and run in spec-only mode:

```powershell
dotnet build src/Soenneker.LinkedIn.Runners.OpenApiClient
$env:ASPNETCORE_ENVIRONMENT = 'Local'
dotnet run --project src/Soenneker.LinkedIn.Runners.OpenApiClient --no-build --no-launch-profile -- --Documentation:SpecOnly=true
```

Output defaults to `output/playwright/`. Configuration uses the `Documentation` section: `SeedUrls` (defaults to the LinkedIn Learn home page), `FollowLinks`, `MarketingVersion` (`YYYYMM`; omitted uses Learn's current version), `MaxPages`, `OutputDirectory`, `BaselineDirectory`, and `FailOnUnresolvedSchemas`. To preview a smaller corpus, supply `--Documentation:FollowLinks=false` and numbered seed arguments such as `--Documentation:SeedUrls:0=https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/posts-api`. Spec-only mode does not clone, generate a client, commit or push. A structurally valid preview can still contain unresolved definitions; review its coverage report before changing the publication gate.
