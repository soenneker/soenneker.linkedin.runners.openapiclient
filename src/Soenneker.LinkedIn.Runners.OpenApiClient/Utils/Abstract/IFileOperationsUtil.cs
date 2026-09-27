using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Utils.Abstract;

public interface IFileOperationsUtil
{
    /// <summary>Builds OpenAPI from official Learn documentation with Playwright. Writes local artifacts in spec-only mode; otherwise fixes the spec, regenerates changed client source and publishes the update.</summary>
    ValueTask Process(CancellationToken cancellationToken = default);
}
