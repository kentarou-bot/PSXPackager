using System.Security.Cryptography;
using System.Text;
using Popstation.Pbp;
using PSXPackager.Common;
using PSXPackager.Common.Cue;

namespace PbpExtract;

/// <summary>
/// Decompresses a plain popstation PBP to the bytes that were packed into it.
/// The source is opened read-only. Protected PSOne Classics images are refused.
/// </summary>
public sealed class PbpImageExtractor
{
    public const int SectorSize = 2352;
    public const int SectorsPerBlock = 16;
    public const int BlockSize = SectorSize * SectorsPerBlock;
    public const int StandardPregapFrames = 150;

    private static readonly byte[] PbpMagic = { 0x00, 0x50, 0x42, 0x50 };
    private static readonly byte[] PgdMagic = { 0x00, 0x50, 0x47, 0x44 };

    public ExtractReport Extract(string inputPath, string outputDirectory, bool overwrite, IReadOnlyCollection<int>? discs)
    {
        var sourceFullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(sourceFullPath))
        {
            throw new FileNotFoundException("Input PBP was not found.", sourceFullPath);
        }

        var sourceInfo = new FileInfo(sourceFullPath);
        var sourceLength = sourceInfo.Length;
        var sourceWriteTime = sourceInfo.LastWriteTimeUtc;

        Directory.CreateDirectory(outputDirectory);
        var outputFull = Path.GetFullPath(outputDirectory);

        var reports = new List<DiscReport>();
        var writtenPaths = new List<string>();

        try
        {
            using var stream = new FileStream(sourceFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            RejectUnlessPlainPopstation(stream);
            stream.Position = 0;

            var reader = new PbpReader(stream);
            var selected = reader.Discs.Where(disc => discs == null || discs.Count == 0 || discs.Contains(disc.Index)).ToList();
            if (selected.Count == 0)
            {
                throw new InvalidOperationException("None of the requested disc numbers are present in this PBP.");
            }

            var stem = Path.GetFileNameWithoutExtension(sourceFullPath);
            foreach (var disc in selected)
            {
                var suffix = reader.Discs.Count == 1 ? "" : $" (Disc {disc.Index})";
                var binPath = Path.Combine(outputFull, stem + suffix + ".bin");
                var cuePath = Path.Combine(outputFull, stem + suffix + ".cue");
                var reportPath = Path.Combine(outputFull, stem + suffix + ".extract.txt");
                EnsureNotSource(sourceFullPath, binPath, cuePath, reportPath);

                var report = WriteDisc(disc, binPath, cuePath, reportPath, sourceFullPath, overwrite);
                reports.Add(report);
                writtenPaths.AddRange(report.OutputPaths);
            }
        }
        catch
        {
            foreach (var path in writtenPaths)
            {
                TryDelete(path);
            }

            throw;
        }

        var after = new FileInfo(sourceFullPath);
        var unchanged = after.Length == sourceLength
            && after.LastWriteTimeUtc == sourceWriteTime;

        if (!unchanged)
        {
            foreach (var path in writtenPaths)
            {
                TryDelete(path);
            }

            throw new IOException("The input PBP changed while it was being read. Output files were removed.");
        }

        return new ExtractReport
        {
            SourcePath = sourceFullPath,
            SourceLength = sourceLength,
            SourceLastWriteTimeUtc = sourceWriteTime,
            SourceUnchanged = true,
            Discs = reports
        };
    }

    private static DiscReport WriteDisc(PbpDiscEntry disc, string binPath, string cuePath, string reportPath, string sourcePath, bool overwrite)
    {
        var available = (long)disc.IsoIndex.Count * BlockSize;
        var leadOutBytes = disc.LeadOutSectors is int sectors and > 0 ? (long)sectors * SectorSize : (long?)null;
        long imageLength;
        string lengthSource;

        if (leadOutBytes is long lead)
        {
            if (lead > available)
            {
                // The tail is absent. A normal popstation pack rounds the image up to a
                // 16-sector block, so the index is longer than the lead-out by at most 15
                // sectors. A longer lead-out is not that padding, and it is not written.
                return WriteCoveredTracks(disc, binPath, reportPath, sourcePath, overwrite, available, lead);
            }

            imageLength = lead;
            lengthSource = "toc-lead-out";
        }
        else if (disc.IsoSize > 0 && disc.IsoSize <= available)
        {
            imageLength = disc.IsoSize;
            lengthSource = "iso9660-volume";
        }
        else
        {
            throw new PbpFormatException(
                $"Disc {disc.Index} has no usable TOC lead-out or ISO9660 size. Nothing was written.");
        }

        if (imageLength % SectorSize != 0)
        {
            throw new PbpFormatException(
                $"Disc {disc.Index} image length {imageLength} is not a multiple of {SectorSize}. Nothing was written.");
        }

        var spans = BuildTrackSpans(disc.TOC, imageLength, out var usedPregap);
        EnsureNotSource(sourcePath, binPath, cuePath, reportPath);
        EnsureCanWrite(overwrite, binPath, cuePath, reportPath);
        var partial = binPath + ".partial";
        TryDelete(partial);

        string md5Hex;
        string shaHex;
        var trackHashes = spans.Select(span => (span, Md5: IncrementalHash.CreateHash(HashAlgorithmName.MD5), Sha: IncrementalHash.CreateHash(HashAlgorithmName.SHA256))).ToList();

        try
        {
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var block = new byte[BlockSize];
                long written = 0;
                for (var i = 0; i < disc.IsoIndex.Count && written < imageLength; i++)
                {
                    uint decoded;
                    try
                    {
                        decoded = disc.ReadBlock(i, block);
                    }
                    catch (Exception ex)
                    {
                        throw new PbpFormatException(
                            $"Disc {disc.Index} block {i} is not an uncompressed popstation block and did not decode as raw deflate. " +
                            "Official encrypted PSOne Classics images are not extracted, and this tool does not repair or pad sectors. Nothing was kept.",
                            ex);
                    }

                    if (decoded != BlockSize)
                    {
                        throw new PbpFormatException(
                            $"Disc {disc.Index} block {i} decoded to {decoded} bytes, not {BlockSize}. " +
                            "The image is not a plain popstation PBP. Nothing was kept.");
                    }

                    var take = (int)Math.Min(decoded, imageLength - written);
                    output.Write(block, 0, take);
                    md5.AppendData(block.AsSpan(0, take));
                    sha.AppendData(block.AsSpan(0, take));

                    var blockStart = written;
                    foreach (var track in trackHashes)
                    {
                        var overlapStart = Math.Max(blockStart, track.span.Offset);
                        var overlapEnd = Math.Min(blockStart + take, track.span.Offset + track.span.Length);
                        if (overlapEnd > overlapStart)
                        {
                            var sliceStart = (int)(overlapStart - blockStart);
                            var sliceLength = (int)(overlapEnd - overlapStart);
                            track.Md5.AppendData(block.AsSpan(sliceStart, sliceLength));
                            track.Sha.AppendData(block.AsSpan(sliceStart, sliceLength));
                        }
                    }

                    written += take;
                }

                if (written != imageLength)
                {
                    throw new PbpFormatException(
                        $"Disc {disc.Index} produced {written} bytes; the TOC or ISO size asked for {imageLength}. Nothing was kept.");
                }
            }

            md5Hex = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            shaHex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            try
            {
                File.Move(partial, binPath, overwrite: true);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            var trackReports = trackHashes.Select(track => new TrackSpanReport
            {
                Number = track.span.Number,
                Type = track.span.Type,
                Status = "complete",
                Offset = track.span.Offset,
                Length = track.span.Length,
                CoveredBytes = track.span.Length,
                MissingBytes = 0,
                Path = binPath,
                Md5 = Convert.ToHexString(track.Md5.GetHashAndReset()).ToLowerInvariant(),
                Sha256 = Convert.ToHexString(track.Sha.GetHashAndReset()).ToLowerInvariant(),
                Note = track.span.Note + " Stored inside the full-disc BIN."
            }).ToList();

            var discReport = new DiscReport
            {
                Index = disc.Index,
                DiscId = disc.DiscID,
                DiscComplete = true,
                BinPath = binPath,
                CuePath = cuePath,
                ReportPath = reportPath,
                BinLength = imageLength,
                AvailableBytes = available,
                ClaimedBytes = imageLength,
                LengthSource = lengthSource,
                Iso9660Bytes = disc.IsoSize,
                TocLeadOutBytes = leadOutBytes,
                Md5 = md5Hex,
                Sha256 = shaHex,
                UsedStandardAudioPregap = usedPregap,
                OutputPaths = new[] { binPath, cuePath, reportPath },
                Tracks = trackReports
            };

            try
            {
                var cue = TOCHelper.TOCtoCUE(disc.TOC, Path.GetFileName(binPath));
                CueFileWriter.Write(cue, cuePath);
                File.WriteAllText(reportPath, FormatReport(discReport), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                TryDelete(binPath);
                TryDelete(cuePath);
                TryDelete(reportPath);
                throw;
            }

            return discReport;
        }
        finally
        {
            foreach (var track in trackHashes)
            {
                track.Md5.Dispose();
                track.Sha.Dispose();
            }
        }
    }

    private static DiscReport WriteCoveredTracks(
        PbpDiscEntry disc,
        string mergedBinPath,
        string reportPath,
        string sourcePath,
        bool overwrite,
        long available,
        long claimed)
    {
        var spans = BuildTrackSpans(disc.TOC, claimed, out var usedPregap);
        var directory = Path.GetDirectoryName(mergedBinPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(mergedBinPath);
        var outputs = new List<string>();
        var trackReports = new List<TrackSpanReport>();

        try
        {
            foreach (var span in spans)
            {
                if (SpanFits(span, available))
                {
                    var path = Path.Combine(directory, $"{stem} (Track {span.Number:00}).bin");
                    var cue = Path.Combine(directory, $"{stem} (Track {span.Number:00}).cue");
                    EnsureNotSource(sourcePath, path, cue);
                    EnsureCanWrite(overwrite, path, cue);
                    var hashes = WriteRangeToFile(disc, span.Offset, span.Length, path);
                    WriteStandaloneCue(cue, Path.GetFileName(path), span);
                    outputs.Add(path);
                    outputs.Add(cue);
                    trackReports.Add(new TrackSpanReport
                    {
                        Number = span.Number,
                        Type = span.Type,
                        Status = "complete",
                        Offset = span.Offset,
                        Length = span.Length,
                        CoveredBytes = span.Length,
                        MissingBytes = 0,
                        Path = path,
                        Md5 = hashes.Md5,
                        Sha256 = hashes.Sha256,
                        Note = span.Note + " Written as its own file. The rest of the disc was not stored."
                    });
                }
                else
                {
                    var covered = CoveredBytes(span, available);
                    trackReports.Add(new TrackSpanReport
                    {
                        Number = span.Number,
                        Type = span.Type,
                        Status = "incomplete",
                        Offset = span.Offset,
                        Length = span.Length,
                        CoveredBytes = covered,
                        MissingBytes = span.Length - covered,
                        Path = "",
                        Md5 = "",
                        Sha256 = "",
                        Note = span.Note + " Not written. The missing bytes are not in the PBP and were not padded."
                    });
                }
            }

            if (trackReports.All(track => track.Status != "complete"))
            {
                throw new PbpFormatException(TruncationMessage(disc.Index, available, claimed, trackReports) +
                    " No track is fully stored, so nothing was written.");
            }

            var report = new DiscReport
            {
                Index = disc.Index,
                DiscId = disc.DiscID,
                DiscComplete = false,
                BinPath = "",
                CuePath = "",
                ReportPath = reportPath,
                BinLength = 0,
                AvailableBytes = available,
                ClaimedBytes = claimed,
                LengthSource = "toc-lead-out",
                Iso9660Bytes = disc.IsoSize,
                TocLeadOutBytes = claimed,
                Md5 = "",
                Sha256 = "",
                UsedStandardAudioPregap = usedPregap,
                OutputPaths = outputs.Append(reportPath).ToArray(),
                Tracks = trackReports
            };

            EnsureNotSource(sourcePath, reportPath);
            EnsureCanWrite(overwrite, reportPath);
            File.WriteAllText(reportPath, FormatReport(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return report;
        }
        catch
        {
            foreach (var path in outputs)
            {
                TryDelete(path);
            }

            TryDelete(reportPath);
            throw;
        }
    }

    private static string TruncationMessage(int discIndex, long available, long claimed, IReadOnlyList<TrackSpanReport> tracks)
    {
        var missing = claimed - available;
        var builder = new StringBuilder();
        builder.Append($"Disc {discIndex} TOC lead-out is {claimed} bytes ({claimed / SectorSize} sectors), ");
        builder.Append($"but the index only stores {available} bytes ({available / SectorSize} sectors). ");
        builder.Append($"The tail is short by {missing} bytes ({missing / SectorSize} sectors). ");
        builder.Append("That is not popstation block padding: padding makes the index longer than the lead-out by at most 15 sectors. ");
        foreach (var track in tracks)
        {
            builder.Append($"Track {track.Number} {track.Type} status={track.Status} offset={track.Offset} length={track.Length} covered={track.CoveredBytes} missing={track.MissingBytes}. ");
        }

        return builder.ToString();
    }

    private static bool SpanFits(TrackSpan span, long available)
    {
        if (span.Offset < 0 || span.Length <= 0 || span.Offset > available)
        {
            return false;
        }

        return span.Length <= available - span.Offset;
    }

    private static long CoveredBytes(TrackSpan span, long available)
    {
        if (span.Offset < 0 || span.Length <= 0 || span.Offset >= available)
        {
            return 0;
        }

        return Math.Min(span.Length, available - span.Offset);
    }

    private static (string Md5, string Sha256) WriteRangeToFile(PbpDiscEntry disc, long offset, long length, string path)
    {
        var partial = path + ".partial";
        TryDelete(partial);
        try
        {
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CopyRange(disc, offset, length, output, md5, sha);
            }

            var md5Hex = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            var shaHex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            File.Move(partial, path, overwrite: true);
            return (md5Hex, shaHex);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void CopyRange(PbpDiscEntry disc, long offset, long length, Stream destination, IncrementalHash md5, IncrementalHash sha)
    {
        var available = (long)disc.IsoIndex.Count * BlockSize;
        if (offset < 0 || length < 0 || offset > available || length > available - offset)
        {
            throw new PbpFormatException("Requested bytes are outside the blocks stored in the PBP.");
        }

        var block = new byte[BlockSize];
        var end = offset + length;
        var position = offset;
        while (position < end)
        {
            var blockNo = (int)(position / BlockSize);
            if (blockNo < 0 || blockNo >= disc.IsoIndex.Count)
            {
                throw new PbpFormatException($"Disc block {blockNo} is outside the index.");
            }

            uint decoded;
            try
            {
                decoded = disc.ReadBlock(blockNo, block);
            }
            catch (Exception ex)
            {
                throw new PbpFormatException(
                    $"Disc {disc.Index} block {blockNo} is not an uncompressed popstation block and did not decode as raw deflate. " +
                    "Official encrypted PSOne Classics images are not extracted, and this tool does not repair or pad sectors. Nothing was kept.",
                    ex);
            }

            if (decoded != BlockSize)
            {
                throw new PbpFormatException(
                    $"Disc {disc.Index} block {blockNo} decoded to {decoded} bytes, not {BlockSize}. Nothing was kept.");
            }

            var inner = (int)(position % BlockSize);
            var take = (int)Math.Min(BlockSize - inner, end - position);
            destination.Write(block, inner, take);
            md5.AppendData(block.AsSpan(inner, take));
            sha.AppendData(block.AsSpan(inner, take));
            position += take;
        }
    }

    private static void WriteStandaloneCue(string cuePath, string binFileName, TrackSpan span)
    {
        var dataType = span.Type == "audio" ? "AUDIO" : "MODE2/2352";
        var builder = new StringBuilder();
        builder.AppendLine($"FILE \"{binFileName}\" BINARY");
        builder.AppendLine($"  TRACK {span.Number:00} {dataType}");
        if (span.Type == "audio" && span.AudioFileIncludesPregap)
        {
            builder.AppendLine("    INDEX 00 00:00:00");
            builder.AppendLine("    INDEX 01 00:02:00");
        }
        else
        {
            builder.AppendLine("    INDEX 01 00:00:00");
        }

        File.WriteAllText(cuePath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static IReadOnlyList<TrackSpan> BuildTrackSpans(IReadOnlyList<TOCEntry> toc, long imageLength, out bool usedStandardAudioPregap)
    {
        usedStandardAudioPregap = false;
        if (toc.Count == 0)
        {
            return new[]
            {
                new TrackSpan(1, "unknown", 0, imageLength, "No TOC tracks were parsed. The hash covers the whole extracted image.", false)
            };
        }

        var starts = new long[toc.Count];
        var notes = new string[toc.Count];
        var audioFileIncludesPregap = new bool[toc.Count];
        var pregapOk = true;
        for (var i = 0; i < toc.Count; i++)
        {
            var index01Frames = ((toc[i].Minutes * 60) + toc[i].Seconds) * 75 + toc[i].Frames;
            var startFrames = (long)index01Frames;
            var note = $"INDEX 01 is at {index01Frames} frames. Span starts at the TOC INDEX 01 position.";
            if (toc[i].TrackType == TrackTypeEnum.Audio && index01Frames >= StandardPregapFrames)
            {
                startFrames = index01Frames - StandardPregapFrames;
                note = $"INDEX 01 is at {index01Frames} frames. Span starts 150 frames (2 seconds) before TOC INDEX 01, which is the usual pregap stored in front of an audio track file.";
                audioFileIncludesPregap[i] = true;
            }

            var start = startFrames * SectorSize;
            if (start < 0 || start > imageLength)
            {
                pregapOk = false;
                break;
            }

            starts[i] = start;
            notes[i] = note;
        }

        if (!pregapOk || !IsStrictlyIncreasing(starts))
        {
            usedStandardAudioPregap = false;
            for (var i = 0; i < toc.Count; i++)
            {
                var index01Frames = ((toc[i].Minutes * 60) + toc[i].Seconds) * 75 + toc[i].Frames;
                starts[i] = Math.Clamp(index01Frames * (long)SectorSize, 0, imageLength);
                notes[i] = $"INDEX 01 is at {index01Frames} frames. Span starts at the TOC INDEX 01 position. The 2-second audio pregap assumption did not produce an ordered layout.";
                audioFileIncludesPregap[i] = false;
            }
        }
        else
        {
            usedStandardAudioPregap = toc.Any(entry => entry.TrackType == TrackTypeEnum.Audio);
        }

        var spans = new List<TrackSpan>(toc.Count);
        for (var i = 0; i < toc.Count; i++)
        {
            var end = i + 1 < toc.Count ? starts[i + 1] : imageLength;
            if (end < starts[i])
            {
                end = starts[i];
            }

            var type = toc[i].TrackType == TrackTypeEnum.Audio ? "audio" : "data";
            spans.Add(new TrackSpan(toc[i].TrackNo, type, starts[i], end - starts[i], notes[i], audioFileIncludesPregap[i]));
        }

        return spans;
    }

    private static bool IsStrictlyIncreasing(long[] starts)
    {
        for (var i = 1; i < starts.Length; i++)
        {
            if (starts[i] <= starts[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    internal static void RejectUnlessPlainPopstation(Stream stream)
    {
        if (stream.Length < 0x28)
        {
            throw new PbpFormatException("The file is too small to be a PBP.");
        }

        var magic = new byte[4];
        stream.Position = 0;
        ReadExactly(stream, magic);
        if (!magic.AsSpan().SequenceEqual(PbpMagic))
        {
            throw new PbpFormatException("The file does not start with the PBP signature 00 50 42 50.");
        }

        stream.Position = 0x24;
        var psarOffset = ReadInt32(stream);
        if (psarOffset <= 0 || psarOffset > stream.Length - 16)
        {
            throw new PbpFormatException("The DATA.PSAR offset does not point inside the file.");
        }

        var signature = new byte[16];
        stream.Position = psarOffset;
        ReadExactly(stream, signature);
        var twelve = Encoding.ASCII.GetString(signature, 0, 12);
        var sixteen = Encoding.ASCII.GetString(signature, 0, 16);
        if (twelve != "PSISOIMG0000" && sixteen != "PSTITLEIMG000000")
        {
            if (signature.AsSpan(0, 4).SequenceEqual(PgdMagic))
            {
                throw Protected();
            }

            throw new PbpFormatException(
                "DATA.PSAR does not start with PSISOIMG0000 or PSTITLEIMG000000. " +
                "PbpExtract only decompresses plain popstation images. Nothing was written.");
        }

        if (psarOffset + 0x54 <= stream.Length)
        {
            var marker = new byte[4];
            stream.Position = psarOffset + 0x50;
            ReadExactly(stream, marker);
            if (marker.AsSpan().SequenceEqual(PgdMagic))
            {
                throw Protected();
            }
        }
    }

    private static PbpFormatException Protected()
    {
        return new PbpFormatException(
            "This PBP contains a PGD marker in DATA.PSAR. That is the protected layout used by official PSOne Classics packages. " +
            "PbpExtract does not decrypt those images, and it does not rebuild ECC/EDC or audio to chase a checksum. Nothing was written.")
        {
            IsProtected = true
        };
    }

    private static string FormatReport(DiscReport disc)
    {
        var builder = new StringBuilder();
        builder.AppendLine("PbpExtract disc report");
        builder.AppendLine($"disc: {disc.Index}");
        builder.AppendLine($"disc-id: {disc.DiscId}");
        builder.AppendLine($"disc-complete: {(disc.DiscComplete ? "true" : "false")}");
        builder.AppendLine($"bin: {disc.BinPath}");
        builder.AppendLine($"cue: {disc.CuePath}");
        builder.AppendLine($"bytes: {disc.BinLength}");
        builder.AppendLine($"available-bytes: {disc.AvailableBytes}");
        builder.AppendLine($"claimed-bytes: {disc.ClaimedBytes}");
        if (!disc.DiscComplete)
        {
            var missing = disc.ClaimedBytes - disc.AvailableBytes;
            builder.AppendLine($"missing-tail-bytes: {missing}");
            builder.AppendLine($"missing-tail-sectors: {missing / SectorSize}");
            builder.AppendLine("The TOC lead-out is longer than the indexed blocks. Those tail sectors are not stored.");
            builder.AppendLine("Normal popstation padding makes the index longer than the lead-out by at most 15 sectors. This gap is the other direction, so it is not alignment padding.");
            builder.AppendLine("Fully stored tracks are separate files. Incomplete tracks were not written and were not padded.");
            builder.AppendLine("This is not a successful full-disc extract.");
        }
        builder.AppendLine($"length-source: {disc.LengthSource}");
        builder.AppendLine($"iso9660-bytes: {disc.Iso9660Bytes}");
        builder.AppendLine($"toc-lead-out-bytes: {(disc.TocLeadOutBytes?.ToString() ?? "none")}");
        builder.AppendLine($"md5: {disc.Md5}");
        builder.AppendLine($"sha256: {disc.Sha256}");
        builder.AppendLine($"audio-pregap-assumption: {(disc.UsedStandardAudioPregap ? "150-frames-before-audio-index-01" : "index-01-only")}");
        builder.AppendLine("note: hashes are of stored bytes only. They are not padded or repaired to match an external checksum.");
        foreach (var track in disc.Tracks)
        {
            builder.AppendLine($"track {track.Number} status={track.Status} type={track.Type} offset={track.Offset} length={track.Length} covered={track.CoveredBytes} missing={track.MissingBytes} md5={track.Md5} sha256={track.Sha256} path={track.Path}");
            builder.AppendLine($"track {track.Number} note: {track.Note}");
        }

        return builder.ToString();
    }

    private static void EnsureNotSource(string source, params string[] outputs)
    {
        foreach (var output in outputs)
        {
            if (string.Equals(Path.GetFullPath(output), source, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Refusing to write the extracted image over the input PBP.");
            }
        }
    }

    private static void EnsureCanWrite(bool overwrite, params string[] paths)
    {
        if (overwrite)
        {
            return;
        }

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                throw new IOException($"Refusing to overwrite existing output '{path}'. Pass --overwrite to replace output files. The input PBP is never overwritten.");
            }
        }
    }

    private static int ReadInt32(Stream stream)
    {
        var buffer = new byte[4];
        ReadExactly(stream, buffer);
        return BitConverter.ToInt32(buffer, 0);
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of PBP.");
            }

            offset += read;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal readonly record struct TrackSpan(int Number, string Type, long Offset, long Length, string Note, bool AudioFileIncludesPregap);
}
