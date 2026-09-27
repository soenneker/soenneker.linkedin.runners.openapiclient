[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/build-and-test.yml?style=for-the-badge)](https://github.com/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/daily-automatic-update.yml?style=for-the-badge&label=Daily%20Update)](https://github.com/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/actions/workflows/daily-automatic-update.yml)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.LinkedIn.Runners.OpenApiClient
### A runner that regenerates and updates Soenneker.LinkedIn.OpenApiClient.

This runner executes a GitHub action that updates another project. It's not meant for consumption.

The runner combines LinkedIn's official Postman collections with its Microsoft Learn documentation. Postman supplies the endpoint foundation, request variants, headers, authentication and saved examples. Playwright reads Learn's schema tables and linked definitions to enrich the request and, especially, response models with documented fields, types, enums and requiredness. Parsing rules are shared across pages rather than maintained per endpoint.

Operations match by method and normalized path, including differing placeholder names and environment-dependent base URLs. Existing collection namespaces remain intact. Learn response schemas enrich matching Postman status codes; an unspecified Learn status can use a single Postman success status. Ambiguous status matches, conflicting types, unmatched operations and unresolved definitions are recorded in `documentation/coverage.json`, along with a count of responses enriched from Learn. Example-only fields do not override schema-table definitions. This does not guarantee complete API coverage.

Each run saves normalized Learn pages and raw Postman collection snapshots, hashes both sources, and reports changes in `documentation/changes.json`. The daily workflow caches this baseline independently of publication and uploads review artifacts. Missing Learn enrichment leaves the Postman operation available. If a collection download fails, a previous snapshot is used with an explicit stale-source warning; without a previous snapshot the update fails. Previously tracked pages or hybrid operations cannot silently disappear. Structural validation failures retain the previous specification. The existing fixer, Kiota and build/push steps run when the published specification changes.

The runner uses `Soenneker.Playwrights.Installation` to ensure Chromium and its dependencies are installed before launching the browser. For a local preview, build and run in spec-only mode:

```powershell
dotnet build src/Soenneker.LinkedIn.Runners.OpenApiClient
$env:ASPNETCORE_ENVIRONMENT = 'Local'
dotnet run --project src/Soenneker.LinkedIn.Runners.OpenApiClient --no-build --no-launch-profile -- --Documentation:SpecOnly=true
```

Output defaults to `output/playwright/`. Configuration uses the `Documentation` section: `SeedUrls` (defaults to the LinkedIn Learn home page), `FollowLinks`, `MarketingVersion` (`YYYYMM`; omitted uses Learn's current version), `MaxPages`, `OutputDirectory`, `BaselineDirectory`, and `FailOnUnresolvedSchemas` (defaults to false; enable for a strict publication gate). To preview a smaller corpus, supply `--Documentation:FollowLinks=false` and numbered seed arguments such as `--Documentation:SeedUrls:0=https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/posts-api`. Spec-only mode does not clone, generate a client, commit or push. For a reproducible local preview, `--Documentation:PostmanDirectory=<directory>` reads the 27 saved `<collection-prefix>.postman.json` files instead of downloading them. This option is restricted to spec-only mode. Review `ResponsesEnrichedFromLearn` and `Issues` in the coverage report to distinguish documented response models from retained Postman examples or missing definitions.
