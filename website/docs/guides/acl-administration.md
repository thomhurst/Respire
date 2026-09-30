---
title: ACL administration
sidebar_position: 13
---

`client.Server` exposes typed ACL inspection and administration. Each ordinary method
runs on one execution node. ACL configuration and logs belong to that node; changing
a Cluster primary does not configure the other members.

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

`AclSetUserAsync`, `AclDeleteUsersAsync`, and `AclLogResetAsync`, including their
all-node variants, require `AllowAdmin = true` before network I/O. Inspection methods
follow the existing read-only Server convention and do not require this client option.
The server still enforces the authenticated user's ACL permissions for every command.

Rules are separate, ordered `RespireValue` arguments. Updates are additive unless a
rule such as `reset` changes that behavior. An empty rule list is valid; an empty
delete-user list is rejected. Usernames, rules, categories, and dry-run arguments are
snapshotted before asynchronous work. They are not routing keys and never receive a
client key prefix. Byte inputs retain their bytes. Compound dry-run descriptors such
as `"CLIENT LIST"` expand into command tokens; each supplied argument remains one token.
`DRYRUN` checks permissions without executing the command. Denials return
`IsAllowed = false` and the server's message. Missing users, invalid commands, and
server errors throw normally. `AclGetUserAsync` returns null for a missing user.

## Owned results and versions

WHOAMI, LIST, GETUSER, SETUSER, DELUSER, CAT, and LOG require Redis 6.0 or later.
DRYRUN and selectors require Redis 7.0 or later. Unsupported commands surface server
errors; the client does not silently emulate security operations.

WHOAMI returns `byte[]`; LIST returns `byte[][]`. User and selector models retain
binary key/channel rules in `RespireAclPatterns`. Redis 6.x supplies `LegacyPatterns`;
Redis 7+ supplies `RuleExpression`. Exactly one is present, and the client does not
split the expression on spaces. Channel rules are absent before Redis 6.2.
Flags, password hashes, and command expressions are strings. Unknown structured
fields are preserved in `AdditionalFields` as recursively copied `RespireResult`
values. These results own GC storage; disposal is optional and invalidates their
element views. The other result models need no disposal and remain valid after the
client is disposed.

`AclCategoriesAsync()` lists categories; supplying a category lists its commands.
`AclLogAsync()` uses the server's default count, and an explicit nonnegative count
limits returned entries. Entries remain newest first. `AclLogResetAsync()` explicitly
clears the log. Log object, username, and client-info fields retain bytes. Entry IDs
and Unix millisecond timestamps are nullable because Redis added them in 7.2.
Unknown reason/context values remain strings instead of being rejected by an enum.

See Redis's [GETUSER](https://redis.io/docs/latest/commands/acl-getuser/),
[SETUSER](https://redis.io/docs/latest/commands/acl-setuser/),
[LOG](https://redis.io/docs/latest/commands/acl-log/), and
[DRYRUN](https://redis.io/docs/latest/commands/acl-dryrun/) references for rule semantics.

## Explicit operations across nodes

Every ACL method has an `OnAllNodesAsync` counterpart. These discover Cluster members,
including replicas, and return `RespireServerResult<T>[]` with endpoint provenance.
Standalone returns one result. Discovery requires permission to inspect `CLUSTER NODES`.
WHOAMI reports the identity authenticated on the connection used for that node.

```csharp
var results = await client.Server.AclGetUserOnAllNodesAsync("reader");
foreach (var result in results)
{
    if (result.IsSuccess)
        Console.WriteLine($"{result.Endpoint}: user present = {result.Value is not null}");
    else
        Console.WriteLine($"{result.Endpoint}: {result.Error!.Message}");
}
```

Inspect every result. All-node mutations can partly succeed and are not atomic or
rolled back. Cancellation during discovery throws; after discovery it is attributed
to affected nodes while completed successes remain available. A successful SETUSER
or LOG RESET node result has `Value = true`; DELUSER reports the count on each node.
These methods do not save an ACL file or guarantee future nodes inherit configuration.

ACL responses can contain password hashes and security details. Do not log rule
arguments or credentials. Respire records operation names rather than adding argument
logging; server error messages remain intact for diagnosis.

The 18 methods extend `IServerCommands`. External implementations, decorators, and
mocks must implement or forward them. These APIs are immediate Server operations;
they do not add ACL methods to batches or transactions.
