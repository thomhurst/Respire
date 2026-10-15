---
title: Migrating earlier package names
description: Update Respire.Extensions package references, namespaces, and assembly identities.
---

# Migrating earlier package names

Some packages were named `Respire.Extensions.*`. The rename changes namespaces and assembly
identities, so rebuild consuming applications and libraries together. The current
[package directory](../packages.md) lists the shipping names and their guides.

- Replace `Respire.Extensions.` with `Respire.` in package references, project paths, and
  `using` directives for renamed packages.
- JSON, Search, TimeSeries, and Probabilistic package IDs are unchanged.
- Replace `using Redis.Search;` with `using Respire.Search;`.
- Import `Respire.Compression.Lz4` for `RespireLz4ValueCodec` and `Respire.Compression.Zstd` for `RespireZstdValueCodec`.
- Update assembly-qualified type names, reflection configuration, and explicit DLL references.
  Do not mix old and renamed packages in one dependency graph.

Upgrade packages that depend on core implementation details together with `Respire`;
see [version alignment](../packages.md#keep-internal-api-consumers-on-the-same-version).
The `Respire.Extensions.<ModuleName>` naming convention is reserved for custom Redis modules
and is not a current shipping package family.
