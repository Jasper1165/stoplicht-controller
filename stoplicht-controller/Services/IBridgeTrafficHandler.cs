using System.Threading;
using System.Threading.Tasks;

namespace stoplicht_controller.Services
{
    public interface IBridgeTrafficHandler
    {
        Task ForceConflictDirectionsToRedAsync(int bridgeDirectionId, CancellationToken token = default);
        Task MakeCrossingGreenAsync(CancellationToken token = default);
    }
}
