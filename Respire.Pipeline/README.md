# Respire Pipeline

A ModularPipelines-based CI/CD pipeline for the Respire Redis client library.

## Features

- **Automated Building**: Builds the entire Respire solution
- **Unit Testing**: Runs all unit tests with comprehensive reporting
- **Benchmarking**: Executes performance benchmarks
- **Version Generation**: Git-based semantic versioning
- **NuGet Packaging**: Creates NuGet packages with proper versioning
- **Local Development**: Supports local NuGet folder for development
- **Production Deployment**: Publishes to NuGet.org when a GitHub release is published

## Configuration

### appsettings.json

Configure the pipeline through `appsettings.json`:

```json
{
  "NuGet": {
    "ApiKey": "your-nuget-api-key"
  },
  "GitHub": {
    "Token": "your-github-token",
    "Repository": "Respire",
    "Owner": "your-github-username"
  },
  "Versioning": {
    "BaseVersion": "0.1.0",
    "ReleaseBranches": [
      "main",
      "master"
    ]
  }
}
```

### User Secrets (Development)

For development, store sensitive information in user secrets:

```bash
dotnet user-secrets set "NuGet:ApiKey" "your-nuget-api-key"
dotnet user-secrets set "GitHub:Token" "your-github-token"
dotnet user-secrets set "GitHub:Owner" "your-github-username"
```

### Environment Variables

You can also use environment variables:

- `NuGet__ApiKey`: NuGet API key
- `GitHub__Token` or `GitHub__AccessToken`: GitHub personal access token
- `GitHub__Owner`: GitHub repository owner
- `Versioning__BaseVersion`: fallback version when no version tag exists

## Usage

### Development Mode

In development mode, packages are copied to a local NuGet folder:

```bash
cd Respire.Pipeline
dotnet run --environment Development
```

This will:
1. Build the solution
2. Run tests
3. Run benchmarks
4. Create packages
5. Register `~/.nuget/local-packages/` as an enabled NuGet source
6. Copy packages to the local folder

Registration uses `dotnet nuget` and its normal configuration hierarchy. An existing
source pointing at the folder is reused, regardless of its name, and enabled if
necessary. Otherwise, the pipeline adds `RespireLocal` (or an unused numbered name
if that name belongs to another source). Repeated runs do not add duplicate sources
or change unrelated feeds. A registration failure stops the dependent local upload.

### Production Mode

Production mode registers `UploadPackagesToNugetModule`, which pushes the packages to NuGet.org with
`--skip-duplicate`. It runs only when `GITHUB_EVENT_NAME` is `release`, so a local
`dotnet run --environment Production` builds, tests and packs but does not publish.

In GitHub Actions, `modular-pipeline.yml` runs in production mode only for a published GitHub release
(see [Releasing](#releasing)). Normal pushes and pull requests run in development mode and do not
publish packages.

## Pipeline Modules

### Core Modules
- `NugetVersionGeneratorModule`: Generates semantic versions based on Git information
- `BuildProjectsModule`: Builds the solution in Release configuration
- `RunUnitTestsModule`: Executes the pipeline's own tests (the library suites run in the CI workflow)
- `PackProjectsModule`: Creates NuGet packages
- `PackagePathsParserModule`: Finds generated package files
- `PackageFilesRemovalModule`: Cleans up old packages

### Production Modules
- `UploadPackagesToNugetModule`: Publishes packages to NuGet.org

### Development Modules
- `CreateLocalNugetFolderModule`: Creates local NuGet package folder
- `AddLocalNugetSourceModule`: Adds local folder as NuGet source
- `UploadPackagesToLocalNuGetModule`: Copies packages to local folder

## Dependencies

The pipeline uses ModularPipelines with the following packages:
- `ModularPipelines.DotNet`: .NET CLI integration
- `ModularPipelines.GitHub`: GitHub Actions environment and Octokit integration

## Versioning Strategy

Every build is versioned as the release it would become: one step above the nearest version tag
reachable from `HEAD`. GitHub creates these tags when a release is published.

- A stable tag moves to its next patch: `v2.0.1` gives `2.0.2`.
- A prerelease tag moves to its next candidate: `v2.0.0-rc.2000` gives `2.0.0-rc.2001`, and
  `v2.0.0-beta` gives `2.0.0-beta.1`.
- The number of commits since the tag does not change the version, and commit messages
  (including `+semver:` markers) have no effect.
- `main`/`master` builds use that version. Other branches add
  `-ci.<branch>.<height>.g<hash>` (or `.ci.<branch>...` after a prerelease), where `height` counts
  commits since the tag.
- With no version tag, `Versioning:BaseVersion` is used.
- On a published release (`GITHUB_EVENT_NAME=release`), the version is the release's tag, which the
  workflow passes in `RESPIRE_RELEASE_VERSION`.

Version tags are SemVer 2.0 versions with an optional `v` prefix and no build metadata: `X.Y.Z` with
an optional prerelease suffix such as `-rc.1`, no leading zeros in numeric parts or numeric
prerelease identifiers, and each of `X`, `Y` and `Z` at most 65534 (they become the assembly
version). Other tags are ignored, and a release whose tag does not match fails. Use full Git history (`fetch-depth: 0`, as in
the workflow).

## Releasing

`.github/workflows/release-draft.yml` keeps one draft GitHub release for `main`, rewritten on every
push. Its tag is one step above the last published release (prereleases included), it targets the
newest `main` commit, and its notes are GitHub's generated notes since that release. The workflow
finds its draft by the hidden `<!-- release-draft -->` comment at the end of the notes. Keep that
comment when you edit the notes: without it, the next push to `main` creates a second draft. It is
removed when the release is published.

1. To release, edit the draft on GitHub. Change its tag to choose the version: keep the suggested
   `v2.0.0-rc.N` for another candidate, use `v2.0.0` to cut the stable release, or `v2.1.0` for a
   minor. Adjust the notes and publish it.
2. Publishing creates the tag on the draft's commit and runs `modular-pipeline.yml` on it. That run
   fails unless the tag is a version and its commit is on `main`. It builds, tests and packs the
   commit with the tag's version, pushes the packages to NuGet.org, and runs the package checks.
3. Once the packages are pushed, the run renames the release to its version, points the generated
   "Full Changelog" compare link at the final tag, marks it as a prerelease when the version has a
   suffix, and appends install commands for the packages between `<!-- nuget-packages:start -->` and
   `<!-- nuget-packages:end -->`. Re-runs replace only that section. This step runs even if a later
   package check fails, and it never fails the release.
4. To retry a failed publish, re-run that workflow run: packages already on NuGet are skipped.

Every push to `main` rewrites the draft, so edits to it are lost if `main` moves before it is
published. Do not move or delete a release tag after publishing; NuGet versions cannot be reused.
Skip the 1.x line: it contains the unlisted, permanently occupied NuGet version `Respire 1.0.116`.

## Customization

You can customize the pipeline by:

1. **Adding new modules**: Create classes inheriting from `Module<T>`
2. **Modifying configuration**: Update `appsettings.json` or use configuration sections
3. **Changing dependencies**: Use `[DependsOn<T>]` attributes to control execution order
4. **Environment-specific behavior**: Use `IHostingEnvironment` for conditional logic

## Example Output

```
info: Building Respire solution...
info: Running unit tests...
info: Running performance benchmarks...
info: Generated version: 0.1.266-ci.feature-optimization.266.a1b2c3d4
info: Packing NuGet packages...
info: Found 2 package files:
info:   - Respire.0.1.266-ci.feature-optimization.266.a1b2c3d4.nupkg (245,760 bytes)
info:   - Respire.DependencyInjection.0.1.266-ci.feature-optimization.266.a1b2c3d4.nupkg (12,345 bytes)
info: Completed copying 2 packages to local NuGet
```
