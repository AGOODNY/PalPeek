namespace PalPeek;

public sealed class DiagnosticsWindowFactory
{
    private readonly DiagnosticsService _diagnostics;

    public DiagnosticsWindowFactory(DiagnosticsService diagnostics) =>
        _diagnostics = diagnostics;

    public DiagnosticsWindow Create() => new(_diagnostics);
}
