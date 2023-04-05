using MimeKit;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using System.Buffers;
using System.Net;
using System.Threading.Channels;

namespace GraphMailRelay
{
	internal class SmtpWorkerRelayStore : MessageStore
	{
		private const string logTraceMessageSaving = "Saving new message to relay store.";
		private const string logTraceMessageQueuing = "Queuing new message.";
		private const string logDebugMessageQueued = "Queued new message.";

		private readonly ILogger<SmtpWorker> _logger;
		private readonly ChannelWriter<KeyValuePair<Guid, MimeMessage>> _queueWriter;

		public SmtpWorkerRelayStore(ILogger<SmtpWorker> logger, ChannelWriter<KeyValuePair<Guid, MimeMessage>> queueWriter)
		{
			_logger = logger;
			_queueWriter = queueWriter;
		}

		public override Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
		{
			// Generate an identifier for this message which will be utilized for tracing message flow through the relay.
			Guid messageId = Guid.NewGuid();

			// Begin logging activities.
			using (_logger.BeginScope(new Dictionary<string, object>
			{
				{ "SenderIP", ((IPEndPoint)context.Properties[EndpointListener.RemoteEndPointKey]).Address.ToString() },
				{ "From", transaction.From.AsAddress() },
				{ "MessageId" , messageId.ToString() }
			}))
			{
				_logger.LogTrace(RelayLogEvents.SmtpWorkerMessageSaving, logTraceMessageSaving);

				// Build message from data buffer.
				var bufferPosition = buffer.GetPosition(0);
				var messageStream = new MemoryStream();

				while (buffer.TryGet(ref bufferPosition, out var memory)) { messageStream.Write(memory.Span); }

				messageStream.Position = 0;

				// Build a new MimeKit message object from the data stream.
				MimeMessage message = MimeMessage.Load(messageStream, cancellationToken);

				using (_logger.BeginScope(new Dictionary<string, object>
				{
					{ "Recipients", string.Join(", ", message.To) }
				}))
				{
					_logger.LogTrace(RelayLogEvents.SmtpWorkerMessageQueuing, logTraceMessageQueuing);

					// Write the message to the relay's queue channel which will later be picked up by the GraphWorker.
					_queueWriter.WriteAsync(new KeyValuePair<Guid, MimeMessage>(messageId, message));

					_logger.LogDebug(RelayLogEvents.SmtpWorkerMessageQueued, logDebugMessageQueued);

					// Return a new task indicating that the "save" operation was successful.
					return Task.FromResult(SmtpResponse.Ok);
				}
			}
		}
	}
}
