using System.Runtime.CompilerServices;

// The projection and contract types are internal by design: they are adapter
// implementation detail, not a public API. Contract tests exercise them
// directly against sanitized bridge fixtures without a Viewer or a GPU.
[assembly: InternalsVisibleTo("NsightAnalyzer.Tests")]
