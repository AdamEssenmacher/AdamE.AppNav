#:property TargetFramework=net10.0
#:property TreatWarningsAsErrors=true

using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class Program
{
    private const string InformationalVersionAttributeName =
        "System.Reflection.AssemblyInformationalVersionAttribute";

    public static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "Usage: verify-package-versions.cs <expected-version> <core-package> <maui-package>");
            return 2;
        }

        try
        {
            Verify(args[0], args[1], args[2]);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Package version verification failed: {exception.Message}");
            return 1;
        }
    }

    private static void Verify(string expectedVersion, string corePackagePath, string mauiPackagePath)
    {
        using ZipArchive corePackage = ZipFile.OpenRead(corePackagePath);
        using ZipArchive mauiPackage = ZipFile.OpenRead(mauiPackagePath);

        PackageMetadata coreMetadata = ReadPackageMetadata(corePackage, "AdamE.AppNav.nuspec");
        PackageMetadata mauiMetadata = ReadPackageMetadata(mauiPackage, "AdamE.AppNav.Maui.nuspec");

        RequireEqual(expectedVersion, coreMetadata.Version, "AdamE.AppNav package version");
        RequireEqual(expectedVersion, mauiMetadata.Version, "AdamE.AppNav.Maui package version");
        RequireEqual(coreMetadata.RepositoryCommit, mauiMetadata.RepositoryCommit, "package repository commits");

        if (string.IsNullOrWhiteSpace(coreMetadata.RepositoryCommit))
        {
            throw new InvalidDataException("Package repository metadata does not contain a commit.");
        }

        if (mauiMetadata.CoreDependencyVersions.Count != 4)
        {
            throw new InvalidDataException(
                $"AdamE.AppNav.Maui must contain four AdamE.AppNav dependency groups; found {mauiMetadata.CoreDependencyVersions.Count}.");
        }

        foreach (string dependencyVersion in mauiMetadata.CoreDependencyVersions)
        {
            RequireEqual(expectedVersion, dependencyVersion, "AdamE.AppNav.Maui dependency on AdamE.AppNav");
        }

        string separator = expectedVersion.Contains('+') ? "." : "+";
        string expectedInformationalVersion = expectedVersion + separator + coreMetadata.RepositoryCommit;

        VerifyAssembly(
            "AdamE.AppNav",
            RequireEntry(corePackage, "lib/net10.0/AdamE.AppNav.dll"),
            expectedInformationalVersion);
        VerifyAssembly(
            "AdamE.AppNav",
            RequireEntry(corePackage, "analyzers/dotnet/cs/AdamE.AppNav.Generators.dll"),
            expectedInformationalVersion);

        VerifyAssembly(
            "AdamE.AppNav.Maui",
            RequireEntry(mauiPackage, "lib/net10.0/AdamE.AppNav.Maui.dll"),
            expectedInformationalVersion);
        VerifyAssembly(
            "AdamE.AppNav.Maui",
            RequireSingleMatchingEntry(
                mauiPackage,
                "^lib/net10\\.0-android[^/]*/AdamE\\.AppNav\\.Maui\\.dll$",
                "the MAUI Android assembly"),
            expectedInformationalVersion);
        VerifyAssembly(
            "AdamE.AppNav.Maui",
            RequireSingleMatchingEntry(
                mauiPackage,
                "^lib/net10\\.0-ios[^/]*/AdamE\\.AppNav\\.Maui\\.dll$",
                "the MAUI iOS assembly"),
            expectedInformationalVersion);
        VerifyAssembly(
            "AdamE.AppNav.Maui",
            RequireSingleMatchingEntry(
                mauiPackage,
                "^lib/net10\\.0-maccatalyst[^/]*/AdamE\\.AppNav\\.Maui\\.dll$",
                "the MAUI Mac Catalyst assembly"),
            expectedInformationalVersion);
        VerifyAssembly(
            "AdamE.AppNav.Maui",
            RequireEntry(mauiPackage, "analyzers/dotnet/cs/AdamE.AppNav.Maui.Generators.dll"),
            expectedInformationalVersion);

        Console.WriteLine(
            $"Package manifests and assembly informational versions match {expectedVersion} at {coreMetadata.RepositoryCommit}.");
    }

    private static PackageMetadata ReadPackageMetadata(ZipArchive package, string nuspecPath)
    {
        ZipArchiveEntry nuspec = RequireEntry(package, nuspecPath);
        using Stream stream = nuspec.Open();
        XDocument document = XDocument.Load(stream, LoadOptions.None);
        XElement metadata = RequireSingleElement(document.Root?.Elements(), "metadata", nuspecPath);
        string version = RequireSingleElement(metadata.Elements(), "version", nuspecPath).Value;
        XElement repository = RequireSingleElement(metadata.Elements(), "repository", nuspecPath);
        string repositoryCommit = repository.Attribute("commit")?.Value ?? string.Empty;
        string[] coreDependencyVersions = metadata
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "dependency" &&
                string.Equals(element.Attribute("id")?.Value, "AdamE.AppNav", StringComparison.Ordinal))
            .Select(element => element.Attribute("version")?.Value ?? string.Empty)
            .ToArray();

        return new PackageMetadata(version, repositoryCommit, coreDependencyVersions);
    }

    private static XElement RequireSingleElement(
        IEnumerable<XElement>? elements,
        string localName,
        string nuspecPath)
    {
        XElement[] matches = elements?
            .Where(element => element.Name.LocalName == localName)
            .ToArray() ?? [];
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"{nuspecPath} must contain exactly one {localName} element; found {matches.Length}.");
        }

        return matches[0];
    }

    private static ZipArchiveEntry RequireEntry(ZipArchive package, string path)
    {
        return package.GetEntry(path) ??
               throw new InvalidDataException($"Package is missing {path}.");
    }

    private static ZipArchiveEntry RequireSingleMatchingEntry(
        ZipArchive package,
        string pattern,
        string description)
    {
        var expression = new Regex(pattern, RegexOptions.CultureInvariant);
        ZipArchiveEntry[] matches = package.Entries
            .Where(entry => expression.IsMatch(entry.FullName))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Package must contain exactly one {description}; found {matches.Length}.");
        }

        return matches[0];
    }

    private static void VerifyAssembly(
        string packageName,
        ZipArchiveEntry assembly,
        string expectedInformationalVersion)
    {
        using Stream source = assembly.Open();
        using var image = new MemoryStream();
        source.CopyTo(image);
        image.Position = 0;

        using var peReader = new PEReader(image);
        if (!peReader.HasMetadata)
        {
            throw new InvalidDataException($"{assembly.FullName} is not a managed assembly.");
        }

        MetadataReader metadataReader = peReader.GetMetadataReader();
        string[] informationalVersions = metadataReader
            .GetAssemblyDefinition()
            .GetCustomAttributes()
            .Select(handle => ReadInformationalVersion(metadataReader, handle))
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        if (informationalVersions.Length != 1)
        {
            throw new InvalidDataException(
                $"{assembly.FullName} must contain exactly one {InformationalVersionAttributeName}; found {informationalVersions.Length}.");
        }

        RequireEqual(
            expectedInformationalVersion,
            informationalVersions[0],
            $"{assembly.FullName} in {packageName}");
    }

    private static string? ReadInformationalVersion(
        MetadataReader metadataReader,
        CustomAttributeHandle handle)
    {
        CustomAttribute attribute = metadataReader.GetCustomAttribute(handle);
        if (!string.Equals(
                ReadAttributeTypeName(metadataReader, attribute.Constructor),
                InformationalVersionAttributeName,
                StringComparison.Ordinal))
        {
            return null;
        }

        BlobReader value = metadataReader.GetBlobReader(attribute.Value);
        if (value.ReadUInt16() != 1)
        {
            throw new InvalidDataException($"{InformationalVersionAttributeName} has an invalid prolog.");
        }

        return value.ReadSerializedString() ??
               throw new InvalidDataException($"{InformationalVersionAttributeName} has a null value.");
    }

    private static string ReadAttributeTypeName(MetadataReader metadataReader, EntityHandle constructor)
    {
        EntityHandle type = constructor.Kind switch
        {
            HandleKind.MemberReference => metadataReader.GetMemberReference(
                (MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadataReader.GetMethodDefinition(
                (MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default
        };

        return type.Kind switch
        {
            HandleKind.TypeReference => ReadTypeReferenceName(metadataReader, (TypeReferenceHandle)type),
            HandleKind.TypeDefinition => ReadTypeDefinitionName(metadataReader, (TypeDefinitionHandle)type),
            _ => string.Empty
        };
    }

    private static string ReadTypeReferenceName(MetadataReader metadataReader, TypeReferenceHandle handle)
    {
        TypeReference type = metadataReader.GetTypeReference(handle);
        return JoinTypeName(metadataReader.GetString(type.Namespace), metadataReader.GetString(type.Name));
    }

    private static string ReadTypeDefinitionName(MetadataReader metadataReader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadataReader.GetTypeDefinition(handle);
        return JoinTypeName(metadataReader.GetString(type.Namespace), metadataReader.GetString(type.Name));
    }

    private static string JoinTypeName(string @namespace, string name)
    {
        return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
    }

    private static void RequireEqual(string expected, string actual, string description)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{description} is '{actual}', expected '{expected}'.");
        }
    }

    private sealed record PackageMetadata(
        string Version,
        string RepositoryCommit,
        IReadOnlyList<string> CoreDependencyVersions);
}
