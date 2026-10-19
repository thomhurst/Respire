# Respire.Serialization.Protobuf

Protobuf implementation of `IRespireSerializer` for .NET 8 and .NET 10.

Set `RespireOptions.Serializer = new RespireProtobufSerializer()`, or register
`services.AddRespireProtobufSerializer()` and resolve `IRespireSerializer` in your
`AddRespire(provider => options with { Serializer = provider.GetRequiredService<IRespireSerializer>() })` factory.
Registration alone does not change existing clients. Options/models must be configured before concurrent use.

Both generic and runtime-Type calls write directly to `IBufferWriter<byte>`.
Reads consume the input span directly. Root null values are rejected because protobuf has no root null representation; empty messages represent default-valued objects. Runtime-Type writes use a cached generic delegate to preserve the declared contract.

The default protobuf-net runtime model and runtime-Type dispatch are not a NativeAOT guarantee. Preserve contract metadata when trimming; use System.Text.Json with a generated context if you require Respire's supported AOT path.

Wire formats are not interchangeable with JSON or other adapters. Use a separate key namespace or migrate stored data when changing serializers.
See the [serialization guide](https://respire.tomlonghurst.dev/docs/fundamentals/values-and-serialization).
