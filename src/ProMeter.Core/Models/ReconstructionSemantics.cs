namespace ProMeter.Models;

/// <summary>
/// Version of the reconstruction semantics that produced a derived row. Rows persisted by an
/// older version keep their evidence but are not trusted for counting until revalidated.
/// </summary>
public static class ReconstructionSemantics
{
    public const int Version = 4;
}
