using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Storage;
using System.Net;

namespace GraphMailRelay
{
	internal class SmtpWorkerFilter : MailboxFilter
	{
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
			string endpointAddress = ((IPEndPoint)context.Properties[EndpointListener.RemoteEndPointKey]).Address.ToString();

			if (@from == Mailbox.Empty) { return MailboxFilterResult.NoPermanently; }

			if (_options.AllowedSenderAddresses is not null)
			{
				if (_options.AllowedSenderAddresses.Contains(endpointAddress))
				{
					return MailboxFilterResult.Yes;
				}
				else
				{
					_logger.LogWarning("Rejecting incoming SMTP message from '{@endpointAddress}'", endpointAddress);
					return MailboxFilterResult.NoTemporarily;
				}
			}
			else
			{
				_logger.LogWarning("Rejecting incoming SMTP message from '{@endpointAddress}'; configuration file(s) do not appear to contain an AllowedSenderAddresses list.", endpointAddress);
				return MailboxFilterResult.NoTemporarily;
			}
		}
	}
}
