using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Soenneker.LinkedIn.Runners.OpenApiClient;

[JsonSerializable(typeof(Documentation.DocumentationPage))]
[JsonSerializable(typeof(Documentation.CrawlFailureReport))]
[JsonSerializable(typeof(Documentation.UnreadablePagesReport))]
[JsonSerializable(typeof(Documentation.RemovedPagesReport))]
[JsonSerializable(typeof(Documentation.ValidationErrorsReport))]
[JsonSerializable(typeof(Documentation.RemovedOperationsReport))]
[JsonSerializable(typeof(Documentation.CoverageReport))]
[JsonSerializable(typeof(Documentation.ChangesReport))]
internal partial class AotJsonContext : JsonSerializerContext
{
    internal static JsonTypeInfo<T> Get<T>(JsonSerializerOptions? options = null) =>
        (JsonTypeInfo<T>)((options is null ? Default : new AotJsonContext(new JsonSerializerOptions(options))).GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"No generated JSON metadata for {typeof(T)}."));
}
