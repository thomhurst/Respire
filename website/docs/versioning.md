---
title: Versioning and compatibility
description: Release versions, release candidates, experimental APIs, and the interface compatibility promise.
---

# Versioning and compatibility

## Release versions

The first supported major release is **2.0.0**. The version skips 1.x because NuGet already
holds an unlisted `Respire 1.0.116` package. Until 2.0.0 ships, the public API can still
change between releases. From 2.0.0 on, Respire follows semantic versioning: breaking changes
only in major releases.

Use the same version for every Respire package in an application. Several add-on packages
pin the exact core version they were built against. See
[choose packages](./packages#upgrade-packages-together).

## Release candidates

Release candidates are published as `2.0.0-rc.N`. To opt in:

```bash
dotnet add package Respire --prerelease
```

Without `--prerelease`, NuGet keeps selecting stable packages. To pin a specific candidate,
use the exact `--version` command from its
[GitHub release](https://github.com/thomhurst/Respire/releases).

## Experimental APIs

A few newer features are marked experimental, and using them needs a one-line opt-in.
They can change in any release. See [experimental APIs](./experimental-apis) for the list.

## Interface compatibility

This section matters if you implement or decorate Respire interfaces such as
`IRespireClient`, for example to add logging or a key prefix.

Command facets, script-cache operations, cache-aside calls, invalidation observation, and
prefix views are required interface members. Custom implementations and decorators must
implement or forward them. A missing member is a compile error. Respire never hides a
missing feature behind a default member that throws `NotSupportedException`.

From 2.0 onward:

- Adding a required member to an existing public interface needs a major release.
- Minor releases can add extension methods built from existing operations, or new optional
  capability interfaces that callers check for explicitly.
- Default interface members are used only when they provide complete behavior on top of the
  existing contract, such as a convenience overload.

### Decorators and physical names

Some integrations need the physical key or channel name after any prefix is applied: cache
coherence, notification-driven coordination waits, streaming dead-letter validation, and the
StackExchange.Redis adapter. `RespireClient` and its prefixed views implement
`IRespireNameResolver`:

```csharp
if (redis is IRespireNameResolver resolver)
{
    RespireKey physicalKey = resolver.ResolveKey("item");
    RespireChannel physicalChannel = resolver.ResolveChannel("events");
}
```

A decorator used with those integrations must also implement `IRespireNameResolver` and
forward both methods to the wrapped client. If the resolver is missing, these integrations
throw `RespireConfigurationException` instead of guessing that there is no prefix.
Decorators that only run commands don't need it.

## Type names

All public Respire types use the `Respire` prefix (`IRespire` for interfaces), so you can
import Respire and StackExchange.Redis in the same file while you migrate. See
[public API naming](./fundamentals/api-naming) for the conventions and the renames from
earlier previews.
