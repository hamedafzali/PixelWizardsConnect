using System.Threading.Tasks;

namespace PixelWizard.Core.Interfaces
{
    public interface IRouterClient
    {
        Task<string> RegisterHostAsync(string routerHost, int routerPort, string hostEndpoint);
        Task<string> ResolveEndpointAsync(string routerHost, int routerPort, string connectionCode);
    }
}
