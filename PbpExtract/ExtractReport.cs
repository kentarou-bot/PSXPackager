namespace PbpExtract;

public sealed class ExtractReport
{
    public required string SourcePath { get; init; }
    public required long SourceLength { get; init; }
    public required DateTime SourceLastWriteTimeUtc { get; init; }
    public required bool SourceUnchanged { get; init; }
    public required IReadOnlyList<DiscReport> Discs { get; init; }
}

public sealed class DiscReport
{
    public required int Index { get; init; }
    public required string DiscId { get; init; }
    public required bool DiscComplete { get; init; }
    public required string BinPath { get; init; }
    public required string CuePath { get; init; }
    public required string ReportPath { get; init; }
    public required long BinLength { get; init; }
    public required long AvailableBytes { get; init; }
    public required long ClaimedBytes { get; init; }
    public required string LengthSource { get; init; }
    public required long Iso9660Bytes { get; init; }
    public required long? TocLeadOutBytes { get; init; }
    public required string Md5 { get; init; }
    public required string Sha256 { get; init; }
    public required bool UsedStandardAudioPregap { get; init; }
    public required IReadOnlyList<string> OutputPaths { get; init; }
    public required IReadOnlyList<TrackSpanReport> Tracks { get; init; }
}

public sealed class TrackSpanReport
{
    public required int Number { get; init; }
    public required string Type { get; init; }
    public required string Status { get; init; }
    public required long Offset { get; init; }
    public required long Length { get; init; }
    public required long CoveredBytes { get; init; }
    public required long MissingBytes { get; init; }
    public required string Path { get; init; }
    public required string Md5 { get; init; }
    public required string Sha256 { get; init; }
    public required string Note { get; init; }
}
