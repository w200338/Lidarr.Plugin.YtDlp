using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
// Primary mitigation is building with -p:AssemblyVersion=1.0.0.0 (see CI),
// which makes the compiler emit 1.0.0.0 everywhere; this tool is the
// belt-and-braces fallback for builds that miss the flag.
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
    // UTF-8 decode/encode of the whole file is lossless, so a string-level
    // replace is safe; each replacement keeps the exact byte length.
    var blobPattern = new Regex(@"Lidarr\.(Core|Common|Http), Version=(\d+(?:\.\d+){0,3})");
    var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
    int blobChanges = 0;

    text = blobPattern.Replace(text, match =>
    {
        var version = match.Groups[2].Value;
        if (version.Length < 7 || !Version.TryParse(version, out var parsed) || parsed <= target)
        {
            return match.Value; // not a 4-part version, or already low enough
        }

        blobChanges++;
        return $"Lidarr.{match.Groups[1].Value}, Version=0.0.0." + new string('0', version.Length - 5);
    });

    if (blobChanges > 0)
    {
        File.WriteAllText(path, text, new UTF8Encoding(false));
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
