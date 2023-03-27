using MimeKit;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using System.Buffers;
using System.Threading.Channels;

namespace GraphMailRelay
{
	internal class SmtpWorkerRelayStore : MessageStore
	{
		private readonly ChannelWriter<KeyValuePair<Guid, MimeMessage>> _queueWriter;

		public SmtpWorkerRelayStore(ChannelWriter<KeyValuePair<Guid, MimeMessage>> queueWriter)
		{
			_queueWriter = queueWriter;
		}

		public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
		{
			// Build message from data buffer.
			var bufferPosition = buffer.GetPosition(0);
			var messageStream = new MemoryStream();

			while (buffer.TryGet(ref bufferPosition, out var memory)) { messageStream.Write(memory.Span); }

			messageStream.Position = 0;

			// Spawn a background thread to handle building the message using MimeKit
			// and writing it into our relay channel queue. This is partially because
			// SaveAsync seems to get upset and cancel the transaction early if this
			// method takes too long to run.
			new Thread(async () =>
			{
				// Build a new MimeKit message object from the data stream.
				MimeMessage message = MimeMessage.Load(messageStream, cancellationToken);

				// Write the message to the relay's queue channel which will later be
				// picked up by the GraphWorker.
				await _queueWriter.WriteAsync(new KeyValuePair<Guid, MimeMessage>(Guid.NewGuid(), message));
			}).Start();

			// Inform the client application that the message was received successfully.
			return SmtpResponse.Ok;
		}
	}
}
