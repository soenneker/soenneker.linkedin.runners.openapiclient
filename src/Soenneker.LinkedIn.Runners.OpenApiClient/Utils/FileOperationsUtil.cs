using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
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
using Soenneker.Utils.File.Download.Abstract;
using Soenneker.Postman.Converter.Abstract;
using Soenneker.OpenApi.Merger.Abstract;
using System.Collections.Generic;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Utils;

public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IPostmanConverter _postmanConverter;
    private readonly IOpenApiMerger _openApiMerger;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IFileDownloadUtil fileDownloadUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IKiotaUtil kiotaUtil, IOpenApiFixer openApiFixer,
        IPostmanConverter postmanConverter, IOpenApiMerger openApiMerger)
    {
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _openApiFixer = openApiFixer;
        _fileDownloadUtil = fileDownloadUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _postmanConverter = postmanConverter;
        _openApiMerger = openApiMerger;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);

        string collectionsDirectory = Path.Combine(gitDirectory, "postman");
        await _directoryUtil.Create(collectionsDirectory, cancellationToken: cancellationToken);
        var mergeInputs = new List<(string prefix, string filePath)>();

        foreach ((string prefix, string collectionId) in Constants.PostmanCollections)
        {
            string collectionPath = Path.Combine(collectionsDirectory, $"{prefix}.postman.json");
            string openApiPath = Path.Combine(collectionsDirectory, $"{prefix}.openapi.json");
            await _fileUtil.DeleteIfExists(collectionPath, cancellationToken: cancellationToken);
            await _fileUtil.DeleteIfExists(openApiPath, cancellationToken: cancellationToken);

            string? downloadedPath = await _fileDownloadUtil.Download($"https://www.postman.com/collections/{collectionId}",
                collectionPath, fileExtension: ".json", cancellationToken: cancellationToken);
            if (downloadedPath == null)
                throw new InvalidOperationException($"LinkedIn Postman collection download failed: {prefix}.");

            await _postmanConverter.SaveOpenApiFile(downloadedPath, openApiPath, cancellationToken);
            mergeInputs.Add((prefix, openApiPath));
        }

        OpenApiDocument merged = await _openApiMerger.MergeOpenApis(mergeInputs, cancellationToken);
        string filePath = Path.Combine(gitDirectory, "openapi.json");
        await _fileUtil.Write(filePath, _openApiMerger.ToJson(merged), true, cancellationToken);
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
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}
