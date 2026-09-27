using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.LinkedIn.Runners.OpenApiClient.Utils.Abstract;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.LinkedIn.Runners.OpenApiClient;

public sealed class ConsoleHostedService : IHostedService
{
    private readonly ILogger<ConsoleHostedService> _logger;

    private readonly IHostApplicationLifetime _appLifetime;
    private readonly IFileOperationsUtil _fileOperationsUtil;

    private int? _exitCode;
    private Task? _executionTask;

    public ConsoleHostedService(ILogger<ConsoleHostedService> logger, IHostApplicationLifetime appLifetime, IFileOperationsUtil fileOperationsUtil)
    {
        _logger = logger;
        _appLifetime = appLifetime;
        _fileOperationsUtil = fileOperationsUtil;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _appLifetime.ApplicationStarted.Register(() =>
        {
            _executionTask = Task.Run(async () =>
            {
                _logger.LogInformation("Running console hosted service ...");
                using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _appLifetime.ApplicationStopping);

                try
                {
                    await _fileOperationsUtil.Process(operationCts.Token);

                    _logger.LogInformation("Complete!");

                    _exitCode = 0;
                }
                catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
                {
                    _exitCode = 130;
                }
                catch (Exception e)
                {
                    if (Debugger.IsAttached)
                        Debugger.Break();

                    _logger.LogError(e, "Unhandled exception");

                    _exitCode = 1;
                }
                finally
                {
                    // Stop the application once the work is done
                    _appLifetime.StopApplication();
                }
            }, cancellationToken);
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executionTask != null)
            await _executionTask.WaitAsync(cancellationToken);
        _logger.LogDebug("Exiting with return code: {exitCode}", _exitCode);

        // Exit code may be null if the user cancelled via Ctrl+C/SIGTERM
        Environment.ExitCode = _exitCode.GetValueOrDefault(-1);
    }
}
