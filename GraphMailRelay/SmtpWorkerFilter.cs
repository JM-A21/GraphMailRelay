using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Storage;
using System.Net;

namespace GraphMailRelay
{
	internal class SmtpWorkerFilter : MailboxFilter
	{
		private const string logTraceMessageReceived = "Received message.";
		private const string logDebugMessageAcceptedWhitelist = "Received message accepted due to whitelist match.";
		private const string logWarningMessageRejectedFromEmpty = "Received message rejected due to missing From address.";
		private const string logWarningMessageRejectedWhitelistEmpty = "Recieved message rejected due to empty sender address whitelist.";
		private const string logWarningMessageRejectedWhitelistMismatch = "Recieved message rejected due to sender address whitelist mismatch.";

		private readonly TimeSpan _delay;
		private readonly SmtpWorkerOptions _options;
		private readonly ILogger<SmtpWorker> _logger;

		public SmtpWorkerFilter(ILogger<SmtpWorker> logger, SmtpWorkerOptions options) : this(TimeSpan.Zero, logger, options) { }
		public SmtpWorkerFilter(TimeSpan delay, ILogger<SmtpWorker> logger, SmtpWorkerOptions options)
		{
			_delay = delay;
			_logger = logger;
			_options = options;
		}

		public override async Task<MailboxFilterResult> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
		{
			await Task.Delay(_delay, cancellationToken);

			// Retrieve endpoint address.
			string endpointAddressRemote = ((IPEndPoint)context.Properties[EndpointListener.RemoteEndPointKey]).Address.ToString();

			// Begin logging activities.
			using (_logger.BeginScope(new Dictionary<string, object>
			{
				{ "SenderIP", endpointAddressRemote },
				{ "From", from.AsAddress() }
			}))
			{
				_logger.LogTrace(RelayLogEvents.SmtpWorkerMessageReceived, logTraceMessageReceived);

				// Reject message with no sender/from address.
				if (@from == Mailbox.Empty)
				{
					_logger.LogDebug(RelayLogEvents.SmtpWorkerMessageRejected, logWarningMessageRejectedFromEmpty);
					return MailboxFilterResult.NoPermanently;
				}

				// Reject message if we have no whitelist to match against.
				if (_options.AllowedSenderAddresses is null)
				{
					_logger.LogWarning(RelayLogEvents.SmtpWorkerMessageRejected, logWarningMessageRejectedWhitelistEmpty);
					return MailboxFilterResult.NoTemporarily;
				}

				// Reject message if the sending endpoint is not in our sender whitelist.
				if (!_options.AllowedSenderAddresses.Contains(endpointAddressRemote))
				{
					_logger.LogWarning(RelayLogEvents.SmtpWorkerMessageRejected, logWarningMessageRejectedWhitelistMismatch);
					return MailboxFilterResult.NoTemporarily;
				}

				// If we reach this point validation should be good. Accept the message.
				_logger.LogDebug(RelayLogEvents.SmtpWorkerMessageAccepted, logDebugMessageAcceptedWhitelist);
				return MailboxFilterResult.Yes;
			}
		}
	}
}
