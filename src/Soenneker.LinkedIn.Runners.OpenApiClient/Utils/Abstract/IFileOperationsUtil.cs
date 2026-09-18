using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Utils.Abstract;

public interface IFileOperationsUtil
{
    /// <summary>Downloads and converts the LinkedIn Postman collections, merges and fixes their OpenAPI documents, then regenerates and publishes the client source.</summary>
    ValueTask Process(CancellationToken cancellationToken = default);
}
