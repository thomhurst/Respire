# Respire Pipeline

A ModularPipelines-based CI/CD pipeline for the Respire Redis client library.

## Features

- **Automated Building**: Builds the entire Respire solution
- **Unit Testing**: Runs all unit tests with comprehensive reporting
- **Benchmarking**: Executes performance benchmarks
- **Version Generation**: Git-based semantic versioning
- **NuGet Packaging**: Creates NuGet packages with proper versioning
- **Local Development**: Supports local NuGet folder for development
- **Production Deployment**: Publishes to NuGet.org and creates GitHub releases

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
- `GitHub__Token`: GitHub personal access token
- `GitHub__Owner`: GitHub repository owner
- `Versioning__BaseVersion`: fallback base version when no version tag exists

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
5. Copy packages to `~/.nuget/local-packages/`
6. Add the local source to NuGet configuration

### Production Mode

In production mode, packages are published to NuGet.org and GitHub releases are created:

```bash
cd Respire.Pipeline
dotnet run --environment Production
```

This will:
1. Build the solution
2. Run tests
3. Run benchmarks  
4. Create packages
5. Upload packages to NuGet.org
6. Create GitHub release

## Pipeline Modules

### Core Modules
- `NugetVersionGeneratorModule`: Generates semantic versions based on Git information
- `BuildProjectsModule`: Builds the solution in Release configuration
- `RunUnitTestsModule`: Executes unit tests
- `RunBenchmarkModule`: Runs performance benchmarks
- `PackProjectsModule`: Creates NuGet packages
- `PackagePathsParserModule`: Finds generated package files
- `PackageFilesRemovalModule`: Cleans up old packages

### Production Modules
- `UploadPackagesToNugetModule`: Publishes packages to NuGet.org
- `CreateGitHubReleaseModule`: Creates GitHub releases with release notes

### Development Modules
- `CreateLocalNugetFolderModule`: Creates local NuGet package folder
- `AddLocalNugetSourceModule`: Adds local folder as NuGet source
- `UploadPackagesToLocalNuGetModule`: Copies packages to local folder

## Dependencies

The pipeline uses ModularPipelines with the following packages:
- `ModularPipelines.DotNet`: .NET CLI integration

## Versioning Strategy

The pipeline generates versions automatically:

- **Main/Master Branch**: `0.1.266` from base version plus commit height
- **Minor Marker**: `0.2.0` when the latest highest marker is `+semver:minor`
- **Feature Branches**: `0.1.266-ci.feature-branch.266.abc12345`
- **No Generated Version**: `0.1.0` fallback

Version is based on:
- Latest version tag, or `Versioning:BaseVersion` when no tag exists
- Commit height since the latest version tag
- Branch name and short commit hash for CI branch packages
- Optional commit message markers: `+semver:major`, `+semver:minor`, `+semver:patch`, and `+semver:none`

If no `+semver:` marker is present, the pipeline applies a patch increment. The highest marker in the commit range wins. Major and minor markers reset lower version parts; patch is then counted from commits after the selected marker.

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
info:   - Respire.Extensions.DependencyInjection.0.1.266-ci.feature-optimization.266.a1b2c3d4.nupkg (12,345 bytes)
info: Completed copying 2 packages to local NuGet
```
