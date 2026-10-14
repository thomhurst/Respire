# Redis recipes

These classes are the source displayed in the website's
[Redis recipes guide](../../website/docs/guides/recipes.mdx). The guide explains key layout,
server requirements, expiry, retries, and failure handling for each recipe.

The integration test project references this project and executes every recipe against
Redis 8.4 with RESP2 and RESP3. With Docker running, from the repository root:

```powershell
& scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'build', 'tests/Respire.IntegrationTests', '-c', 'Release', '-f', 'net10.0')
& scripts/Invoke-AgentDotNet.ps1 -DotNetArguments @(
    'run', '--project', 'tests/Respire.IntegrationTests', '-c', 'Release', '-f', 'net10.0',
    '--no-build', '--', '--treenode-filter', '/*/*/RecipeIntegrationTests/*',
    '--minimum-expected-tests', '14')
```

Testcontainers owns the Redis container and removes it when the tests finish. The samples
are a class library, so applications can copy a recipe or reference this project and call
it using their own long-lived client. `EnvironmentConnection.ConnectAsync()` creates that
client from `REDIS_URL`, falling back to `VALKEY_URL` when the first variable is blank.
