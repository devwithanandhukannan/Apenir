using System.Threading;
using System.Threading.Tasks;

namespace Apenir.API.BackgroundServices
{
    public interface IWhatsAppWebhookQueue
    {
        void QueueWebhook(string payload);
        ValueTask<string> DequeueAsync(CancellationToken cancellationToken);
    }
}
