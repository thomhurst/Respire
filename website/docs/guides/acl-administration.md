---
title: ACL administration
sidebar_position: 13
---

`client.Server` exposes typed ACL inspection and administration. Each ordinary method runs on one node. ACL configuration and logs are per node: changing a Cluster primary does not configure the other members.

```csharp
await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    AllowAdmin = true,
});

await client.Server.AclSetUserAsync("reader",
    ["reset", "on", ">a-secret-from-your-secret-store", "~orders:*", "+get"]);
RespireAclUser? user = await client.Server.AclGetUserAsync("reader");
RespireAclDryRunResult check = await client.Server.AclDryRunAsync(
    "reader", RespireCommands.String.GET, ["orders:42"]);
if (!check.IsAllowed) Console.WriteLine(check.DenialReason);
```

- `AclSetUserAsync`, `AclDeleteUsersAsync`, and `AclLogResetAsync` (and their all-node variants) require `AllowAdmin = true`, or throw `NotSupportedException` before any I/O. Read-only methods do not.
- The server still enforces the authenticated user's ACL permissions.
- Rules are separate, ordered `RespireValue` arguments. Updates add to the user unless a rule such as `reset` says otherwise. Redis validates syntax; bad rules return its error unchanged.
- An empty rule list is valid; an empty delete-user list is rejected.
- Usernames, rules, categories, and dry-run arguments are snapshotted before async work. They are not routing keys and never get a key prefix. Byte inputs keep their bytes.
- Compound dry-run commands such as `"CLIENT LIST"` expand into command tokens; each supplied argument stays one token.
- `DRYRUN` checks permissions without running the command. A denial returns `IsAllowed = false` and the server's message. Missing users, invalid commands, and server errors throw.
- `AclGetUserAsync` returns null for a missing user.

## Owned results and versions

| Commands | Minimum Redis |
| --- | --- |
| WHOAMI, LIST, GETUSER, SETUSER, DELUSER, CAT, LOG | 6.0 |
| DRYRUN, selectors | 7.0 |

Unsupported commands return the server error; security operations are never emulated.

Results:

- WHOAMI returns `byte[]`; LIST returns `byte[][]`.
- User and selector models keep binary key and channel rules in `RespireAclPatterns`. Redis 6.x fills `LegacyPatterns`; Redis 7+ fills `RuleExpression` (not split on spaces). Exactly one is set. Channel rules are absent before Redis 6.2.
- Flags, password hashes, and command expressions are strings.
- Unknown fields go to `AdditionalFields` as `RespireResult` values. Disposing them is optional and invalidates their element views. Other models need no disposal and stay valid after the client is disposed.

Categories and log:

- `AclCategoriesAsync()` lists categories; pass a category to list its commands.
- `AclLogAsync()` uses the server's default count. A nonnegative count limits entries; zero returns empty without resetting. Entries are newest first.
- `AclLogResetAsync()` clears the log.
- Log object, username, and client-info fields keep their bytes. Entry IDs and Unix-millisecond timestamps are nullable (added in Redis 7.2). Unknown reason and context values stay strings.

See Redis's [GETUSER](https://redis.io/docs/latest/commands/acl-getuser/),
[SETUSER](https://redis.io/docs/latest/commands/acl-setuser/),
[LOG](https://redis.io/docs/latest/commands/acl-log/), and
[DRYRUN](https://redis.io/docs/latest/commands/acl-dryrun/) references for rule semantics.

## Explicit operations across nodes

Every ACL method has an `OnAllNodesAsync` counterpart. It discovers Cluster members, including replicas (needs permission for `CLUSTER NODES`), and returns `RespireServerResult<T>[]` tagged by endpoint. Standalone returns one result. WHOAMI reports the identity on the connection used for each node. Here `redis` is a client with `AllowAdmin = true`:

```csharp
var results = await redis.Server.AclGetUserOnAllNodesAsync("reader");
foreach (var result in results)
{
    if (result.IsSuccess)
        Console.WriteLine($"{result.Endpoint}: user present = {result.Value is not null}");
    else
        Console.WriteLine($"{result.Endpoint}: {result.Error!.Message}");
}
```

- Check every result. All-node mutations can partly succeed; they are not atomic and are not rolled back.
- Cancellation during discovery throws. After discovery it is recorded per node, and completed results stay available.
- A successful SETUSER or LOG RESET returns `Value = true`; DELUSER returns the count per node.
- These methods do not save an ACL file, and future nodes do not inherit the configuration.

:::warning
ACL responses can contain password hashes and security details. Do not log rule arguments or credentials. Respire records operation names only; server error messages are kept intact.
:::

The 18 ACL methods (nine single-node methods and their all-node counterparts) are `IServerCommands` members. External implementations, decorators, and mocks must implement or forward them. They are immediate Server operations, not available in batches or transactions.
