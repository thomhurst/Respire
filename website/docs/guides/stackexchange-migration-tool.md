---
title: StackExchange.Redis migration code fix
description: Opt in to semantic migration diagnostics and convert supported IDatabase methods to native Respire APIs.
---

# StackExchange.Redis migration code fix

The `Respire` package includes an opt-in Roslyn analyzer and code fix that rewrites classic
StackExchange.Redis `IDatabase` calls as native Respire calls, straight from the IDE lightbulb.
It resolves real StackExchange.Redis symbols, so unrelated methods with matching names are ignored,
and it only offers a fix when the result compiles and behaves the same. It targets the 3.x
`IDatabase` API; the 4.0 command-group API is not covered.

## Install and enable

Keep your existing StackExchange.Redis reference and add Respire:

```bash
dotnet add package Respire
```

The analyzer is bundled under `analyzers/dotnet/cs`; there is no separate
`Respire.Analyzers` NuGet package. Do not exclude analyzer assets from the reference.
Use a Roslyn code-fix host such as Visual Studio 2022 17.8 or later, with an SDK that
supports your application's target framework.

Enable these rules in the project's `.editorconfig` while migrating:

```ini
[*.cs]
dotnet_diagnostic.RESP007.severity = suggestion
dotnet_diagnostic.RESP008.severity = warning
```

- **RESP007** marks the first supported call in a method. Use the lightbulb action
  **Migrate method and IDatabase parameter to RespireClient**. It changes the parameter
  type and every database call in that method together.
- **RESP008** marks calls requiring manual migration, including `GetDatabase` connection
  setup. It offers no automatic fix.

Both rules are disabled by default. Remove the opt-in after completing migration.
Fix All supports document, project, and solution scopes. It validates the combined edits
before offering them, including signature collisions between otherwise valid fixes.

## Supported boundary and calls

Automatic conversion currently targets static async methods with exactly one `IDatabase`
parameter, used only as the receiver of supported calls. There must be no nested lambdas
or local functions, parameter attributes/defaults, preprocessor directives, other database
receivers, extension methods, or partial-method declaration. Calls
must be immediately awaited; `ConfigureAwait(bool)` is preserved. The parameter becomes
`global::Respire.RespireClient`, retaining its name and nullable annotation.

| StackExchange.Redis expression | Converted Respire expression |
| --- | --- |
| `(string?)await db.StringGetAsync(key)` | `await db.Strings.GetStringAsync(key)` |
| `(byte[]?)await db.StringGetAsync(key)` | `await db.Strings.GetBytesAsync(key)` |
| `await db.StringSetAsync(key, "value")` | `await db.Strings.SetAsync(key, "value")` |
| `await db.KeyExistsAsync(key)` | `await db.Keys.ExistsAsync(key)` |
| `await db.KeyDeleteAsync(key)` when its Boolean result is used | `(await db.Keys.DeleteAsync(key, cancellationToken: default) != 0)` |
| `await db.KeyDeleteAsync(key);` with the result discarded | `await db.Keys.DeleteAsync(key, cancellationToken: default);` |

Keys must be string expressions. SET values must be non-null constant strings, including
constant expressions. Named key/value arguments retain their evaluation order. GET requires
an explicit string or byte-array cast: missing keys remain null, empty values remain empty,
and the byte-array path does not decode binary data as text.

Any explicit optional argument requires manual review, including `CommandFlags.None`,
expiry, `When`, and `keepTtl`. So do array overloads, `RedisKey`/`RedisValue` arguments,
binary keys, nonconstant or nullable SET values, unawaited tasks, `RedisValue` results,
instance methods, database aliases, batches, transactions, and other commands. A null
StackExchange.Redis SET value can delete a key; the tool never rewrites it to a native SET
that rejects null. It never drops an option to make a call fit.

### When RESP007 shows no fix

The fix is deliberately conservative, so RESP007 can appear without a lightbulb action. It is withheld when:

- the edited document or its partial-type declarations have compiler errors, or the project has no Respire reference;
- the method's name is used anywhere else in the project or its dependent projects (including
  generated source), because changing its signature could make another call pick a different
  overload or become ambiguous;
- a dependent project is not C#.

Fix the compiler errors, or migrate callers first, or convert that method by hand. Callers outside
the loaded solution cannot be checked: changing a public method's parameter is a breaking change for its consumers.

## Example

Before:

<!-- doc-test-declaration -->
```csharp
using StackExchange.Redis;

public static async Task<string?> ReadAsync(IDatabase database, string key)
{
    return (string?)await database.StringGetAsync(key).ConfigureAwait(false);
}
```

After applying the fix:

<!-- doc-test-declaration -->
```csharp
public static async Task<string?> ReadAsync(global::Respire.RespireClient database, string key)
{
    return await database.Strings.GetStringAsync(key).ConfigureAwait(false);
}
```

## Configure the host explicitly

Connection creation, lifetime, dependency injection, endpoint selection and logical database
selection remain manual work. A call such as `connection.GetDatabase(3)` raises RESP008;
the tool never replaces it with a client using database zero. Configure the corresponding
Respire client with the same database and intended topology before passing it to a migrated
method. Review routing, key prefixes, caching, retry policy, value codecs and exception
handling as part of that setup. See [connections](../fundamentals/connections) and the
[API comparison and migration examples](../stackexchange-redis).

For code that must retain StackExchange.Redis contracts during a gradual migration, use the
[incremental migration adapter](./stackexchange-interop).
