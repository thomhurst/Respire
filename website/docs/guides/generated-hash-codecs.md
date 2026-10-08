---
title: Generated hash codecs
---

`[RespireHash]` generates a companion codec for a partial class or record class. The generator ships with the `Respire` package and emits direct property access, constructor calls and scalar parsing. It does not use reflection or dynamic serialization.

<!-- doc-test-top-level-tail-declaration: split-before=[RespireHash -->
```csharp
using Respire;

var user = new User("42", "Ada", null);
RespireKey key = UserHashMapper.GetKey(user); // user:{42}
Dictionary<string, string> fields = UserHashMapper.ToFields(user);
User copy = UserHashMapper.FromFields(fields);

[RespireHash("user:{{{Id}}}")]
public partial record User(string Id, string Name, string? SessionToken);
```

The companion lives in the model's namespace and has the same public or internal accessibility. `ToFields` returns a new mutable dictionary with ordinal field names. `FromFields` accepts an `IReadOnlyDictionary<string, string>`; use an ordinal dictionary for case-sensitive Redis field semantics. Unknown fields are ignored.

## Values and missing fields

Public instance properties support `string`, `bool`, `int`, `long`, `double`, `decimal`, `Guid` and `DateTimeOffset`, including nullable variants. Field names are property names.

| Property type | Stored text |
| --- | --- |
| `string` | Unchanged text, including empty strings and Unicode |
| `bool` | `1` or `0`; other representations are rejected when reading |
| `int`, `long`, `decimal` | Invariant numeric text |
| `double` | Invariant round-trip text, including .NET's `NaN` and infinity representations |
| `Guid` | `D` format |
| `DateTimeOffset` | `O` format, preserving the offset |

Null properties are omitted. Empty strings remain present, so null and empty values round-trip separately. A missing nullable field decodes to null. Missing non-nullable fields throw `FormatException`; malformed text throws the scalar parser's `FormatException` or `OverflowException`. A runtime null in a non-nullable string property throws `ArgumentException` when encoding or expanding a key. Null codec arguments throw `ArgumentNullException`.

Numeric decoding uses .NET's invariant-culture parsers with explicit styles. Integers allow a leading sign; `double` and `decimal` also allow a decimal point and exponent. Numeric text containing whitespace or thousands separators is rejected. Encoding always writes invariant numeric text without thousands separators.

`ToFields` produces a representation; it does not replace an existing Redis hash. Writing the returned fields with `HSET` alone cannot remove fields whose new value is null. A caller writing an existing hash must handle those deletions explicitly. Neither helper offers partial-read or change-tracking semantics.

## Keys and supported models

Templates substitute non-nullable mapped properties by their exact name. Repeated placeholders are supported. Use `{{` and `}}` for literal braces, including Redis cluster hash tags as in the example. Values are inserted unchanged; the codec does not escape property values or add a client key prefix. A constant non-empty key template is also supported.

Models must be top-level, non-generic, public or internal partial classes or record classes, with no base class other than `object`. Every public instance property needs public `get` and `set`/`init` accessors. Property names must be distinct ignoring case. Public instance fields, indexers, unsupported property types, abstract classes, record structs and nested models produce diagnostic `RESP004`.

There is no property opt-out attribute in this initial codec. A computed get-only property, collection or nested object therefore prevents generation; use a separate scalar model when those properties are needed elsewhere.

Constructors may be `public`, `internal` or `protected internal`, because the mapper lives in the same assembly. An accessible parameterless constructor is preferred. Otherwise, constructor parameters must match public properties by name (ignoring case) and exact type, including nullability; the matching constructor with the fewest parameters is selected. Positional records satisfy this rule. The codec passes decoded values to that constructor and assigns remaining properties in an object initializer. Constructor-bound properties are also assigned when C# required-member rules demand it; a constructor marked `[SetsRequiredMembers]` avoids that additional assignment. Custom constructors and accessors execute normally; their behavior remains the model author's responsibility. Private members and static properties are not mapped. A pre-existing `ModelNameHashMapper` type is rejected.

These codecs are the first deliverable of [the object mapper work](https://github.com/thomhurst/Respire/issues/895). Generated Redis I/O, partial reads, field TTL, change tracking, JSON mapping, Search schemas, Native AOT sample execution and benchmark parity remain separately tracked there. Direct code generation alone does not establish final Native AOT or performance acceptance.
