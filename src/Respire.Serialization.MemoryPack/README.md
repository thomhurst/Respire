# Respire.Serialization.MemoryPack

MemoryPack implementation of `IRespireSerializer` for .NET 8 and .NET 10.

Set `RespireOptions.Serializer = new RespireMemoryPackSerializer()`, or register
`services.AddRespireMemoryPackSerializer()` and resolve `IRespireSerializer` in your
`AddRespire(provider => options with { Serializer = provider.GetRequiredService<IRespireSerializer>() })` factory.
Registration alone does not change existing clients. Options/models must be configured before concurrent use.

Both generic and runtime-Type calls write directly to `IBufferWriter<byte>`.
Reads consume the input span directly. Mark your models [MemoryPackable] and partial so MemoryPack generates formatters.

Generated formatters are required for trimming/AOT. Reflection-based fallback and runtime-Type dispatch are not guaranteed AOT-safe. The adapter retains IRespireSerializer's trimming/dynamic-code warnings; validate your concrete model graph in a published application.

Wire formats are not interchangeable with JSON or other adapters. Use a separate key namespace or migrate stored data when changing serializers.
See the [serialization guide](https://thomhurst.github.io/Respire/docs/fundamentals/values-and-serialization).
