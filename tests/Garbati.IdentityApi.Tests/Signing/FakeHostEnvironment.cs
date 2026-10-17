using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Garbati.IdentityApi.Tests.Signing;

// Minimal stand-in for IHostEnvironment, so SigningKeyOptionsValidatorTests can drive its
// Production-environment check without spinning up a real host.
public sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Garbati.IdentityApi.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}
