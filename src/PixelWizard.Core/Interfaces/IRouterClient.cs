using System.Threading.Tasks;

namespace PixelWizard.Core.Interfaces
{
    public sealed record RouterRegistrationResult(string ConnectionCode, string SessionSecret);
    public sealed record RouterConnectResult(string HostEndpoint, string SessionSecret);

    public interface IRouterClient
    {
        Task<RouterRegistrationResult> RegisterHostAsync(string routerHost, int routerPort, string hostEndpoint);
        Task<RouterConnectResult> ResolveEndpointAsync(string routerHost, int routerPort, string connectionCode);
    }
}
