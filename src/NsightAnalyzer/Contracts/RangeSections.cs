namespace NsightAnalyzer.Contracts;

[Flags]
public enum RangeSections
{
    None = 0,
    Metrics = 1,
    Shaders = 2,
    InstructionMix = 4,
    Timing = 8,
    All = Metrics | Shaders | InstructionMix | Timing,
}

internal static class RangeSectionNames
{
    public static string[] For(RangeSections sections) =>
        new[]
        {
            (RangeSections.Timing, "timing"),
            (RangeSections.Metrics, "metrics"),
            (RangeSections.Shaders, "shaders"),
            (RangeSections.InstructionMix, "instruction-mix"),
        }.Where(item => sections.HasFlag(item.Item1)).Select(item => item.Item2).ToArray();

    public static bool TryParse(string text, out RangeSections sections)
    {
        sections = RangeSections.None;
        foreach (var part in text.Split(','))
        {
            var section = part switch
            {
                "timing" => RangeSections.Timing,
                "metrics" => RangeSections.Metrics,
                "shaders" => RangeSections.Shaders,
                "instruction-mix" => RangeSections.InstructionMix,
                _ => RangeSections.None,
            };
            if (section == RangeSections.None || sections.HasFlag(section))
            {
                return false;
            }
            sections |= section;
        }
        return sections != RangeSections.None;
    }
}
