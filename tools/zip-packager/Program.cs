using System.IO.Compression;

// Creates (or replaces) a zip with the given files at the zip root.
// Usage: zip-packager <output.zip> <file1> [file2 ...]
// Exit codes: 0 ok, 1 usage error, 2 missing input file.
var argv = Environment.GetCommandLineArgs()[1..];
if (argv.Length < 2)
{
    Console.Error.WriteLine("usage: zip-packager <output.zip> <file> [file ...]");
    return 1;
}

var output = Path.GetFullPath(argv[0]);
var entries = argv[1..];

foreach (var e in entries)
{
    if (!File.Exists(e))
    {
        Console.Error.WriteLine($"missing input file: {e}");
        return 2;
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
if (File.Exists(output))
{
    File.Delete(output);
}

using var zip = File.Create(output);
using var archive = new ZipArchive(zip, ZipArchiveMode.Create);
foreach (var entry in entries)
{
    // Entry name is the file name only → files sit at the zip root.
    archive.CreateEntryFromFile(Path.GetFullPath(entry), Path.GetFileName(entry), CompressionLevel.Optimal);
}

Console.WriteLine($"created {output} with {entries.Length} entr{(entries.Length == 1 ? "y" : "ies")}");
return 0;
