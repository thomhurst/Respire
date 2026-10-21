using System.Diagnostics.CodeAnalysis;

namespace Respire;

/// <summary>A binary field/value pair owned by a Path Hash reply.</summary>
public readonly record struct RespirePathHashField(
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Reply fields expose owned bytes without copying on access.")]
    byte[] Field,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Reply values expose owned bytes without copying on access.")]
    byte[] Value);

/// <summary>A binary path and its owned field/value payload.</summary>
public sealed record RespirePathHashEntry(
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Reply paths expose owned bytes without copying on access.")]
    byte[] Path,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Reply payload exposes owned storage without copying on access.")]
    RespirePathHashField[] Fields);

/// <summary>A Path Hash page with an opaque continuation token. Cursor "0" marks completion.</summary>
public sealed record RespirePathHashPage(string Cursor,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Reply page exposes owned storage without copying on access.")]
    RespirePathHashEntry[] Entries);

/// <summary>Fields to write at one binary path. Arrays are borrowed until the immediate call completes; queues snapshot them.</summary>
public readonly record struct RespirePathHashWrite(RespireKey Path,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Input descriptor deliberately borrows the caller's field array.")]
    (RespireKey Field, RespireValue Value)[] Fields);

/// <summary>Fields to read at one binary path, in the requested order.</summary>
public readonly record struct RespirePathHashRead(RespireKey Path,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Input descriptor deliberately borrows the caller's field array.")]
    RespireKey[] Fields);
