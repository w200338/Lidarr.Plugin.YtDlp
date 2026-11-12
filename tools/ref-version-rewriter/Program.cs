using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

// Rewrites the assembly-reference versions of the host-contract assemblies
// (Lidarr.Core / Lidarr.Common / Lidarr.Http) in the plugin DLL, plus the
// assembly-qualified names embedded in custom-attribute blobs.
//
// Why: the plugin compiles against the Lidarr source submodule, whose
// Directory.Build.props sets <AssemblyVersion>10.0.0.*</AssemblyVersion>
// ("should be replaced by CI" - the official CI stamps the release version).
// A locally built submodule therefore makes the plugin reference e.g.
// Lidarr.Core 10.0.0.17987, while an installed host (v3.1.6.5079) loads
// Lidarr.Core 3.1.6.5079. The .NET runtime binds a requested host assembly
// to the already-loaded one only when the loaded version is >= the requested
// version; a HIGHER requested version fails with
//   FileNotFoundException: Lidarr.Core, Version=10.0.0.17987
// and the host aborts startup. (Verified empirically against the official
// v3.1.6.5079 host: a plugin referencing 2.1.1.0 or 1.0.0.0 loads; one
// referencing 10.0.0.x does not. This is also why Tubifarry - which carries
// its own plugin version, e.g. 2.1.1.0, as the host reference version -
// loads fine on the user's 3.1.6.5079 host.)
//
// SECOND PROBLEM (attribute blobs): the C# compiler also writes full
// assembly-qualified type names into custom-attribute blobs, e.g.
//   [FieldDefinition(Type = FieldType.Textbox)] encodes
//   "NzbDrone.Core.Annotations.FieldType, Lidarr.Core, Version=10.0.0.21720, ..."
// with the COMPILE-TIME version. Those strings bypass the assembly-reference
// table (they are opaque bytes to the reference loader) and are resolved
// verbatim by the host's schema builder (TypeNameParser) when it reads the
// provider settings types. A compile-time version of 10.0.0.x therefore
// also breaks GET /api/v1/{indexer,downloadclient}/schema with the same
// FileNotFoundException even though the plugin assembly itself loads.
// This tool patches those embedded versions to a same-length zero version
// so the blob byte lengths (and length prefixes) stay intact.
//
// Stamping 1.0.0.0 keeps the reference below every released (and plausible
// future) host assembly version, so the plugin binds against whatever host
// it is installed in.
//
// Usage: ref-version-rewriter <dll> [version]
//   version defaults to 1.0.0.0
//
// The DLL is rewritten to a temp path and moved into place, so an
// interrupted run can never leave a truncated plugin DLL behind.

var hostAssemblies = new[] { "Lidarr.Core", "Lidarr.Common", "Lidarr.Http" };

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: ref-version-rewriter <dll> [version]");
    return 2;
}

var path = Path.GetFullPath(args[0]);
var target = new Version(args.Length > 1 ? args[1] : "1.0.0.0");

if (!File.Exists(path))
{
    Console.Error.WriteLine($"file not found: {path}");
    return 1;
}

var tmp = path + ".rewrite.tmp";
var changed = new List<string>();

try
{
    using var assembly = AssemblyDefinition.ReadAssembly(path);
    foreach (var module in assembly.Modules)
    {
        foreach (var reference in module.AssemblyReferences.ToList())
        {
            if (!hostAssemblies.Contains(reference.Name))
            {
                continue;
            }

            var old = reference.Version;
            reference.Version = target;
            if (old != target)
            {
                changed.Add($"{reference.Name}: {old} -> {target}");
            }
        }
    }

    assembly.Write(tmp, new WriterParameters { WriteSymbols = false });
    File.Move(tmp, path, overwrite: true);

    // Second pass: patch host versions embedded in custom-attribute blobs.
    // Pure byte-level in-place overwrites: locate the ASCII prefix
    // "Lidarr.<name>, Version=", parse the version string that follows, and
    // when it is higher than the target, overwrite the version BYTES with a
    // same-length zero form. Every replacement is exactly as long as the
    // original, so metadata blob length prefixes stay valid, and no other
    // byte of the file is touched - unlike a whole-file string decode/encode
    // pass, which corrupts binary regions that are not valid UTF-8 (and any
    // per-replacement length error shifts every length prefix after it).
    var blobBytes = File.ReadAllBytes(path);
    int blobChanges = 0;

    foreach (var name in hostAssemblies)
    {
        var prefix = Encoding.ASCII.GetBytes($"{name}, Version=");
        int pos = 0;
        while ((pos = IndexOf(blobBytes, prefix, pos)) >= 0)
        {
            int vStart = pos + prefix.Length;
            int vEnd = vStart;
            while (vEnd < blobBytes.Length && IsVersionByte(blobBytes[vEnd]))
            {
                vEnd++;
            }

            int vLen = vEnd - vStart;
            if (vLen > 0 && Version.TryParse(Ascii(blobBytes, vStart, vLen), out var parsed) && parsed > target)
            {
                var replacement = SameLengthZeroForm(Ascii(blobBytes, vStart, vLen));
                for (int k = 0; k < vLen; k++)
                {
                    blobBytes[vStart + k] = (byte)replacement[k];
                }

                blobChanges++;
                pos = vEnd; // continue scanning after the patched version
            }
            else
            {
                pos++; // advance at least one byte
            }
        }
    }

    if (blobChanges > 0)
    {
        File.WriteAllBytes(path, blobBytes);
        Console.WriteLine($"patched {blobChanges} embedded attribute-blob host version string(s)");
    }
}
catch (Exception ex)
{
    if (File.Exists(tmp))
    {
        File.Delete(tmp);
    }

    Console.Error.WriteLine($"rewrite failed: {ex.Message}");
    return 1;
}

static int IndexOf(byte[] haystack, byte[] needle, int from)
{
    if (needle.Length == 0 || from < 0 || from + needle.Length > haystack.Length)
    {
        return -1;
    }

    for (int i = from; i <= haystack.Length - needle.Length; i++)
    {
        bool ok = true;
        for (int j = 0; j < needle.Length; j++)
        {
            if (haystack[i + j] != needle[j])
            {
                ok = false;
                break;
            }
        }

        if (ok)
        {
            return i;
        }
    }

    return -1;
}

static bool IsVersionByte(byte b) => b is >= (byte)'0' and <= (byte)'9' || b == (byte)'.';

static string Ascii(byte[] bytes, int start, int len) =>
    new string(Enumerable.Range(start, len).Select(i => (char)bytes[i]).ToArray());

/// <summary>Same-length replacement that Version.TryParse reads as 0.0.0.0.</summary>
static string SameLengthZeroForm(string version)
{
    if (version.Length < 6)
    {
        return new string('0', version.Length);
    }

    return "0.0.0." + new string('0', version.Length - 6);
}

foreach (var line in changed)
{
    Console.WriteLine(line);
}

if (changed.Count == 0)
{
    Console.WriteLine("no reference changes needed (already at target version)");
}

// A host-contract reference is only rewritten if the code actually uses it
// (e.g. the plugin may never touch Lidarr.Http). Missing is not an error.
return 0;
