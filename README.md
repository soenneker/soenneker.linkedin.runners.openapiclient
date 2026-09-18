[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/build-and-test.yml?style=for-the-badge)](https://github.com/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/daily-automatic-update.yml?style=for-the-badge&label=Daily%20Update)](https://github.com/soenneker/Soenneker.LinkedIn.Runners.OpenApiClient/actions/workflows/daily-automatic-update.yml)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.LinkedIn.Runners.OpenApiClient
### A runner that regenerates and updates Soenneker.LinkedIn.OpenApiClient.

This runner executes a GitHub action that updates another project. It's not meant for consumption.

The runner downloads the public LinkedIn Postman collections listed in `Constants.PostmanCollections` on each run, converts each with `Soenneker.Postman.Converter`, merges them with `Soenneker.OpenApi.Merger`, and passes the merged document through `Soenneker.OpenApi.Fixer` before Kiota generation and the existing build/push step. Collection prefixes are passed directly to the merger for namespacing. No collection snapshots or custom conversion logic are required in this repository.
