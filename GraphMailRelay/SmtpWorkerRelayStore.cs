using MimeKit;
using SmtpServer;
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
		private readonly ILogger<SmtpWorker> _logger;
		private readonly ChannelWriter<KeyValuePair<Guid, MimeMessage>> _queueWriter;

		public SmtpWorkerRelayStore(ILogger<SmtpWorker> logger, ChannelWriter<KeyValuePair<Guid, MimeMessage>> queueWriter)
		{
			_logger = logger;
			_queueWriter = queueWriter;
		}

		public override Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
		{
			// Build message from data buffer.
			var bufferPosition = buffer.GetPosition(0);
			var messageStream = new MemoryStream();

			while (buffer.TryGet(ref bufferPosition, out var memory)) { messageStream.Write(memory.Span); }

			messageStream.Position = 0;

			// Build a new MimeKit message object from the data stream.
			MimeMessage message = MimeMessage.Load(messageStream, cancellationToken);

			// Generate an identifier for this message which will be utilized for tracing message flow through the relay.
			Guid messageIdentifier = Guid.NewGuid();

			// Write a log entry indicating that we're queuing this message for the GraphWorker.
			string componentName = GetType().Name;
			string endpointAddress = ((IPEndPoint)context.Properties[EndpointListener.RemoteEndPointKey]).Address.ToString();
			_logger.LogDebug("{@componentName} is queueing message '{@messageIdentifier}' received from {@endpointAddress}", componentName, messageIdentifier, endpointAddress);

			// Write the message to the relay's queue channel which will later be picked up by the GraphWorker.
			_queueWriter.WriteAsync(new KeyValuePair<Guid, MimeMessage>(messageIdentifier, message));

			// Return a new task indicating that the "save" operation was successful.
			return Task.FromResult(SmtpResponse.Ok);
		}
	}
}
