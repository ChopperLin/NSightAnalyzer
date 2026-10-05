using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Adapters.NsightViewer2026_2;

internal sealed record ShaderSourceSelection(
    EventKey Scope,
    ShaderKey Shader,
    int HashMatchOccurrence);
