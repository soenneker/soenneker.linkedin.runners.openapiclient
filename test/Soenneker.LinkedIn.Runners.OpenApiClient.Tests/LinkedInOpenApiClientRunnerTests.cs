using Soenneker.Tests.HostedUnit;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class LinkedInOpenApiClientRunnerTests : HostedUnitTest
{
    public LinkedInOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
