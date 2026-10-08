namespace Respire;

/// <summary>A generated partial hash read's selection, presence and decoded value.</summary>
/// <typeparam name="T">The model property's type.</typeparam>
/// <param name="Selected">Whether this property was requested.</param>
/// <param name="Found">Whether Redis returned a value for the requested property.</param>
/// <param name="Value">The decoded value, or default when unselected or missing.</param>
public readonly record struct RespireHashField<T>(bool Selected, bool Found, T? Value);
