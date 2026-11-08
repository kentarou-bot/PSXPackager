using System.Text;

namespace PbpExtract;

internal static class Program
{
    private const int Ok = 0;
    private const int Error = 1;
    private const int Protected = 2;
    private const int Invalid = 3;
    private const int Partial = 4;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Any(arg => arg is "--help" or "-h" or "/?"))
            {
                Console.WriteLine(Help());
                return args.Length == 0 ? Invalid : Ok;
            }

            string? input = null;
            string? output = null;
            var overwrite = false;
            var discs = new List<int>();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--input" or "-i":
                        input = Next(args, ref i, arg);
                        break;
                    case "--output" or "-o":
                        output = Next(args, ref i, arg);
                        break;
                    case "--disc" or "-d":
                        foreach (var part in Next(args, ref i, arg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!int.TryParse(part, out var disc) || disc < 1 || disc > 5)
                            {
                                Console.Error.WriteLine("Disc numbers must be from 1 to 5.");
                                return Invalid;
                            }

                            discs.Add(disc);
                        }
                        break;
                    case "--overwrite":
                        overwrite = true;
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown argument '{arg}'.");
                        Console.Error.WriteLine(Help());
                        return Invalid;
                }
            }

            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            {
                Console.Error.WriteLine("Both --input and --output are required.");
                Console.Error.WriteLine(Help());
                return Invalid;
            }

            var report = new PbpImageExtractor().Extract(input, output, overwrite, discs);
            Print(report);
            return report.Discs.All(disc => disc.DiscComplete) ? Ok : Partial;
        }
        catch (PbpFormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.IsProtected ? Protected : Invalid;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Invalid;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Invalid;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Error;
        }
    }

    private static string Next(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {name}.");
        }

        index++;
        return args[index];
    }

    private static void Print(ExtractReport report)
    {
        Console.WriteLine("PbpExtract");
        Console.WriteLine($"source: {report.SourcePath}");
        Console.WriteLine($"source-bytes: {report.SourceLength}");
        Console.WriteLine($"source-unchanged: {report.SourceUnchanged}");
        foreach (var disc in report.Discs)
        {
            Console.WriteLine($"disc {disc.Index} id={disc.DiscId} disc-complete={(disc.DiscComplete ? "true" : "false")} available-bytes={disc.AvailableBytes} claimed-bytes={disc.ClaimedBytes}");
            if (disc.DiscComplete)
            {
                Console.WriteLine($"  bin: {disc.BinPath}");
                Console.WriteLine($"  bytes: {disc.BinLength}");
                Console.WriteLine($"  md5: {disc.Md5}");
                Console.WriteLine($"  sha256: {disc.Sha256}");
            }
            else
            {
                var missing = disc.ClaimedBytes - disc.AvailableBytes;
                Console.WriteLine($"  full disc was not written. missing-tail-bytes={missing} missing-tail-sectors={missing / PbpImageExtractor.SectorSize}");
            }

            foreach (var track in disc.Tracks)
            {
                Console.WriteLine($"  track {track.Number} {track.Status} {track.Type} offset={track.Offset} bytes={track.Length} covered={track.CoveredBytes} missing={track.MissingBytes} md5={track.Md5}");
                if (!string.IsNullOrEmpty(track.Path))
                {
                    Console.WriteLine($"    path: {track.Path}");
                }
            }

            Console.WriteLine($"  report: {disc.ReportPath}");
        }

        Console.WriteLine("Hashes are stored bytes only. This tool does not pad or edit them to match another checksum.");
    }

    private static string Help()
    {
        var builder = new StringBuilder();
        builder.AppendLine("PbpExtract — read-only PBP to BIN/CUE decompression");
        builder.AppendLine();
        builder.AppendLine("Usage:");
        builder.AppendLine("  dotnet PbpExtract.dll --input <file.pbp> --output <directory> [--disc 1] [--overwrite]");
        builder.AppendLine();
        builder.AppendLine("The input file is opened read-only and is never replaced.");
        builder.AppendLine("Plain popstation PSISOIMG images are decompressed.");
        builder.AppendLine("Official PSOne Classics PBPs with a PGD-protected PSAR are refused.");
        builder.AppendLine("Output bytes are not padded or repaired to force an external checksum.");
        builder.AppendLine("Exit 0 writes the full disc. Exit 4 writes only tracks whose bytes are fully stored");
        builder.AppendLine("when the TOC lead-out is longer than the index. Exit 3 writes nothing.");
        builder.AppendLine();
        builder.AppendLine("Run this DLL with the signed Microsoft .NET runtime. There is no native apphost.");
        return builder.ToString();
    }
}
