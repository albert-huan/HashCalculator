using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HashCalculator;
using HashCalculator.ViewModels.Windows;

internal static class Program
{
    private static readonly byte[] Data = { 10, 20, 30 };
    private static readonly byte[] Digest = Enumerable.Repeat((byte)1, 32).ToArray();
    private static string Hex => Convert.ToHexString(Digest);

    [STAThread]
    private static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Settings.Current.ResetTemplatesForChecklist();
        var workspace = Directory.CreateTempSubdirectory("hashcalculator-regression-");
        try
        {
            TestPaths(workspace.FullName);
            TestAlgorithms();
            TestCsv(workspace.FullName);
            TestCopy(workspace.FullName);
            TestCancellation(workspace.FullName);
            TestMarkAndRestore();
            Console.WriteLine("Six regression scenarios passed.");
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static void TestPaths(string root)
    {
        string a = Directory.CreateDirectory(Path.Combine(root, "a")).FullName;
        string b = Directory.CreateDirectory(Path.Combine(root, "b")).FullName;
        foreach (string dir in new[] { root, a, b })
            File.WriteAllBytes(Path.Combine(dir, "same.bin"), Data);
        File.WriteAllBytes(Path.Combine(a, "unique.bin"), Data);
        File.WriteAllBytes(Path.Combine(b, "missing.bin"), Data);
        foreach (string dir in new[] { a, b })
            File.WriteAllBytes(Path.Combine(dir, "ambiguous.bin"), Data);

        string manifest = Path.Combine(root, "checks.hcb");
        File.WriteAllText(manifest, $"#SHA256 {Hex} ./same.bin\n#SHA256 {Hex} ambiguous.bin\n" +
            $"#SHA256 {Hex} ./missing.bin\n#SHA256 {Hex} a//./unique.bin\n");
        var checklist = HashChecklist.File(manifest);
        Require(checklist.ReasonForFailure == null, "HCB parse");
        Require(checklist.TryGetFileHashChecker("same.bin", out _), "dot-relative entry");
        Require(!checklist.TryGetFileHashChecker(@"a\same.bin", out _), "root entry matched nested file");
        Require(checklist.TryGetFileHashChecker(@"a\unique.bin", out _), "nested path normalization");
        Require(checklist.TryGetFileHashChecker(Path.Combine(a, "unique.bin"), "unique.bin", out _),
            "use checklist root even when a row has a different root");
        Require(!checklist.TryGetFileHashChecker(Path.Combine(Path.GetDirectoryName(root)!, "same.bin"),
            "same.bin", out _), "match outside checklist root");
        Require(!checklist.AddChecklistItem("SHA256", Hex, @"a\..\same.bin"), "parent traversal");
        var rows = new PathPackage(root, root, checklist, SearchMethod.Descendants).ToArray();
        Require(rows.Length == 4 && rows.Count(row => !row.Deprecated) == 2, "resolved and unresolved rows");
        Require(rows.Any(row => row.FileName == "ambiguous.bin" && row.Deprecated), "ambiguous name");
        Require(rows.Any(row => row.FileName == "missing.bin" && row.Deprecated), "missing explicit path");

        File.WriteAllText(manifest, $"#SHA256 {Hex} unique.bin\n");
        checklist = HashChecklist.File(manifest);
        rows = new PathPackage(root, root, checklist, SearchMethod.Descendants).ToArray();
        Require(rows.Length == 1 && rows[0].FilePath == Path.Combine(a, "unique.bin") && !rows[0].Deprecated,
            "unique basename in a subdirectory");
        Require(checklist.TryGetFileHashChecker(@"a\unique.bin", out _), "resolved basename lookup");
    }

    private static void TestAlgorithms()
    {
        var checklist = new HashChecklist { AlgoTypesFromOption = new[] { AlgoType.SHA_256 } };
        Require(checklist.AddChecklistItem("", Hex, "file.bin"), "unlabelled SHA-256 parse");
        Require(checklist.TryGetFileHashChecker("file.bin", out var checker), "unlabelled SHA-256 lookup");
        byte[] other = new byte[32];
        Require(checker.ComparisonResultOf(AlgoType.SHA_256, other) == CmpRes.Mismatch, "one candidate");

        checklist = new HashChecklist();
        Require(checklist.AddChecklistItem("", Hex, "file.bin"), "ambiguous digest parse");
        Require(checklist.TryGetFileHashChecker("file.bin", out checker), "ambiguous digest lookup");
        Require(checker.ComparisonResultOf(AlgoType.SHA_256, other) == CmpRes.Uncertain, "ambiguous mismatch");
        Require(checker.ComparisonResultOf(AlgoType.SHA_256, Digest) == CmpRes.Matched, "ambiguous match");
        Require(checklist.AddChecklistItem("", Convert.ToHexString(other), "file.bin"), "conflicting digest");
        Require(checker.ComparisonResultOf(AlgoType.SHA_256, Digest) == CmpRes.Uncertain,
            "one matching digest cannot override conflicting entries");
    }

    private static void TestCsv(string root)
    {
        string csv = Path.Combine(root, "checks.csv");
        string[] names = { "-report.bin", "=report.bin", "+report.bin", "@report.bin", "file,1.bin" };
        foreach (string name in names)
            File.WriteAllBytes(Path.Combine(root, name), Data);
        string[] fields = names.Select(name => AlgoInOutModel.EscapeCsvValue(name, relativeFilePath: true)).ToArray();
        Require(fields.Take(4).All(field => field.StartsWith("\".\\", StringComparison.Ordinal)),
            "CSV formula protection");
        File.WriteAllLines(csv, fields.Select(field => $"SHA256,{Hex},{field}"));
        var checklist = HashChecklist.File(csv);
        Require(checklist.ReasonForFailure == null, "CSV parse");
        foreach (string name in names)
            Require(checklist.TryGetFileHashChecker(name, out _), $"CSV round trip: {name}");

        const string encodedName = "\u4e2d\u6587.bin";
        File.WriteAllText(csv, $"SHA256,{Hex},{encodedName}\n", Encoding.GetEncoding(54936));
        checklist = HashChecklist.File(csv);
        Require(checklist.SourceDirectory == root, "encoding fallback lost the checklist directory");
    }

    private static void TestCopy(string root)
    {
        string target = Path.Combine(root, "copy.bin");
        MarkedFileBatch.WriteCopy(root, "copy.bin", output =>
        {
            File.WriteAllText(target, "keep");
            output.Write(Data);
        }, CancellationToken.None);
        Require(File.ReadAllText(target) == "keep", "preserve target created during output");
        Require(File.ReadAllBytes(Path.Combine(root, "copy_1.bin")).SequenceEqual(Data), "publish complete copy");
        try
        {
            MarkedFileBatch.WriteCopy(root, "failed.bin", output =>
            {
                output.WriteByte(1);
                throw new IOException("simulated failed write");
            }, CancellationToken.None);
            throw new InvalidOperationException("Expected write failure.");
        }
        catch (IOException e) when (e.Message == "simulated failed write") { }
        Require(!File.Exists(Path.Combine(root, "failed.bin")) && !Directory.EnumerateFiles(root, "*.tmp").Any(),
            "cleanup after failed write");
    }

    private static void TestCancellation(string root)
    {
        using var source = new MemoryStream(new byte[8 * 1024 * 1024]);
        var progress = new ProgressWindowModel();
        int updates = 0;
        progress.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(progress.CurrentValue))
            {
                updates++;
                progress.TokenSrc.Cancel();
            }
        };
        try
        {
            MarkedFileBatch.WriteCopy(root, "cancelled.bin", output =>
                new HcmDataHelper(source).GenerateMarkedFile(output, null, progress), progress.TokenSrc.Token);
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException) { }
        Require(updates == 1 && source.Position > 0 && source.Position < source.Length,
            "cancellation interrupts current copy");
        Require(!File.Exists(Path.Combine(root, "cancelled.bin")) && !Directory.EnumerateFiles(root, "*.tmp").Any(),
            "cleanup after cancellation");
    }

    private static void TestMarkAndRestore()
    {
        foreach (byte[] bytes in new[] { Data, Array.Empty<byte>() })
        {
            using var source = new MemoryStream(bytes);
            using var marked = new MemoryStream();
            using var restored = new MemoryStream();
            var progress = new ProgressWindowModel();
            Require(new HcmDataHelper(source).GenerateMarkedFile(marked, null, progress), "write HCM marker");
            var helper = new HcmDataHelper(marked);
            Require(helper.ReadHcmData(out var info) && info.DataReliable && info.Position == bytes.Length,
                "read HCM marker and original length");
            Require(helper.RestoreMarkedFile(restored, info, progress) && restored.ToArray().SequenceEqual(bytes),
                "restore original bytes");
        }
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Regression: {scenario}");
    }
}
