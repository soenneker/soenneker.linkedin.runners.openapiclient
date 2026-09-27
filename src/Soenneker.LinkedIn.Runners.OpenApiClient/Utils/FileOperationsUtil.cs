using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.LinkedIn.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using System.Collections.Generic;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Utils;

public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IDocumentationSpecGenerator _specGenerator;
    private readonly DocumentationOptions _documentationOptions;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IFileUtil fileUtil, IDirectoryUtil directoryUtil, IKiotaUtil kiotaUtil, IOpenApiFixer openApiFixer,
        IDocumentationSpecGenerator specGenerator, IConfiguration configuration)
    {
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _openApiFixer = openApiFixer;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _specGenerator = specGenerator;
        _documentationOptions = configuration.GetSection("Documentation").Get<DocumentationOptions>() ?? new DocumentationOptions();
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        _documentationOptions.Validate();
        if (_documentationOptions.SpecOnly)
        {
            string output = _documentationOptions.OutputDirectory ?? Path.Combine(Environment.CurrentDirectory, "output", "playwright");
            await _specGenerator.Generate(output, _documentationOptions, cancellationToken);
            _logger.LogInformation("Specification and coverage report saved to {OutputDirectory}", Path.GetFullPath(output));
            return;
        }
        _documentationOptions.BaselineDirectory ??= _documentationOptions.OutputDirectory ?? Path.Combine(Environment.CurrentDirectory, "output", "playwright");
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);
        bool changed;
        bool generated = false;
        try
        {
            changed = await _specGenerator.Generate(gitDirectory, _documentationOptions, cancellationToken);
            generated = true;
        }
        catch (DocumentationReviewRequiredException)
        {
            generated = true;
            throw;
        }
        finally
        {
            // Keep review artifacts outside the temporary checkout, including when coverage prevents publication.
            string artifacts = _documentationOptions.OutputDirectory ?? Path.Combine(Environment.CurrentDirectory, "output", "playwright");
            string documentation = Path.Combine(gitDirectory, "documentation");
            if (Directory.Exists(documentation))
            {
                Directory.CreateDirectory(Path.Combine(artifacts, "documentation"));
                if (generated && !File.Exists(Path.Combine(documentation, "crawl-failure.json")))
                    File.Delete(Path.Combine(artifacts, "documentation", "crawl-failure.json"));
                foreach (string file in Directory.EnumerateFiles(documentation, generated ? "*.json" : "crawl-failure.json"))
                    File.Copy(file, Path.Combine(artifacts, "documentation", Path.GetFileName(file)), true);
                if (generated && File.Exists(Path.Combine(gitDirectory, "openapi.json")))
                    File.Copy(Path.Combine(gitDirectory, "openapi.json"), Path.Combine(artifacts, "openapi.json"), true);
            }
        }
        if (!changed)
        {
            _logger.LogInformation("The documentation-derived specification is unchanged; skipping client generation.");
            await CommitAndPush(gitDirectory, cancellationToken);
            return;
        }
        string filePath = Path.Combine(gitDirectory, "openapi.json");
        string fixedFilePath = Path.Combine(gitDirectory, "openapi.fixed.json");
        await _fileUtil.DeleteIfExists(fixedFilePath, cancellationToken: cancellationToken);
        await _openApiFixer.Fix(filePath, fixedFilePath, cancellationToken).NoSync();

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.Generate(fixedFilePath, "LinkedInOpenApiClient", Constants.Library, gitDirectory, cancellationToken).NoSync();

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            // Process children before parents without allocating LINQ sorting buffers.
            dirs.Sort(static (left, right) => right.Length.CompareTo(left.Length));
            foreach (string dir in dirs)
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            throw new InvalidOperationException("The generated LinkedIn client did not build successfully.");
        }

        await CommitAndPush(gitDirectory, cancellationToken);
    }

    private async ValueTask CommitAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}
