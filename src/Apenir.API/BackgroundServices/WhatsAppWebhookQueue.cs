using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Apenir.API.BackgroundServices
{
    public class WhatsAppWebhookQueue : IWhatsAppWebhookQueue
    {
        private readonly Channel<string> _channel;

        public WhatsAppWebhookQueue()
        {
            _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        public void QueueWebhook(string payload)
        {
            _channel.Writer.TryWrite(payload);
        }

        public ValueTask<string> DequeueAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAsync(cancellationToken);
        }
    }
}
