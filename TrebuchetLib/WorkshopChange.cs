namespace TrebuchetLib;

/// <summary>One change note of a workshop file: what its author wrote for an update (Steam's BBCode, as written).</summary>
public sealed record WorkshopChange(DateTime AtUtc, string Notes);
