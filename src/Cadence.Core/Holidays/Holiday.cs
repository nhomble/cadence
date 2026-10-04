namespace Cadence.Core.Holidays;

/// <summary>A day off. <see cref="Date"/> is the OBSERVED date; the name gets " (observed)" when it differs from the actual date.</summary>
public sealed record Holiday(DateOnly Date, string Name);
