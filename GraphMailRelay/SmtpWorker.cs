using MimeKit;
using Org.BouncyCastle.Utilities.Net;
using SmtpServer;
using System.Text;
using System.Threading.Channels;

namespace GraphMailRelay
{
	internal class SmtpWorker : IHostedService, IDisposable
	{
		private CancellationTokenSource? _stoppingCts;
		private Task? _workerTask;
		private bool disposedValue;

		private readonly IHostApplicationLifetime _applicationLifetime;
		private readonly ILogger<SmtpWorker> _logger;
		private readonly SmtpWorkerOptions _options;
		private readonly ChannelWriter<KeyValuePair<Guid, MimeMessage>> _queueWriter;

		private SmtpServer.SmtpServer? _smtpServer;

		public SmtpWorker(
			IHostApplicationLifetime applicationLifetime,
			ILogger<SmtpWorker> logger,
			SmtpWorkerOptions optionsSmtp,
			Channel<KeyValuePair<Guid, MimeMessage>> queue)
		{
			_applicationLifetime = applicationLifetime;
			_logger = logger;
			_options = optionsSmtp;
			_queueWriter = queue.Writer;
		}

		// // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
		// ~SmtpWorker()
		// {
		//     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
		//     Dispose(disposing: false);
		// }

		public async Task StartAsync(CancellationToken cancellationToken)
		{
			_logger.LogTrace(RelayLogEvents.SmtpWorkerStarting, "Worker is starting.");

			// Create linked token to allow cancelling executing task from provided token
			_stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			// Confirm provided configuration parameters are valid.
			if (ValidateOptions())
			{
				// Begin setup of the SmtpServer object that will handle receiving mail.

				var smtpServiceOptions = new SmtpServerOptionsBuilder()
					.ServerName(_options.ServerName!)
					.Port((int)_options.ServerPort!)
					.Build();

				var smtpServiceProvider = new SmtpServer.ComponentModel.ServiceProvider();
				smtpServiceProvider.Add(new SmtpWorkerFilter(_logger, _options));
				smtpServiceProvider.Add(new SmtpWorkerRelayStore(_logger, _queueWriter));

				_smtpServer = new SmtpServer.SmtpServer(smtpServiceOptions, smtpServiceProvider);

				// Start the SmtpServer.
				_workerTask = _smtpServer.StartAsync(CancellationToken.None);

				string serverWhitelist = string.Join("\r\n\t", _options.AllowedSenderAddresses!.Select(address => address));
				string serverName = _options.ServerName!;
				int serverPort = (int)_options.ServerPort!;
				_logger.LogInformation(RelayLogEvents.SmtpWorkerStarted, "Worker is listening on {@serverName}:{@serverPort}.\r\nIncoming mail will be accepted from the following whitelisted endpoints:\r\n\t{@serverWhitelist}", serverName, serverPort, serverWhitelist);
			}
			else
			{
				_logger.LogWarning(RelayLogEvents.SmtpWorkerCancelling, "Worker is requesting application halt.");
				_applicationLifetime.StopApplication();
			}
		}

		private bool ValidateOptions()
		{
			_logger.LogTrace(RelayLogEvents.SmtpWorkerValidating, "Worker is validating.");

			// Define some lists and variables we'll use later to write missing or invalid settings to the log and abort application startup.
			List<string> optionsMissing = new();
			List<string> optionsInvalid = new();
			bool optionsValidationFailed = false;

			// Begin validation of provided configuration parameters.

			// Validate serverName.
			if (_options.ServerName is not null)
			{
				if (!Uri.CheckHostName(_options.ServerName).Equals(UriHostNameType.Dns))
				{
					optionsInvalid.Add(string.Format("{0}:ServerName ('{1}' is not a valid DNS host name)", SmtpWorkerOptions.SmtpConfiguration));
					optionsValidationFailed = true;
				}
			}
			else
			{
				optionsMissing.Add(string.Format("{0}:ServerName", SmtpWorkerOptions.SmtpConfiguration));
				optionsValidationFailed = true;
			}

			// Validate serverPort.
			if (_options.ServerPort is not null)
			{
				var serverPortsWellKnown = new List<int> { 25, 465, 587 };

				if (!(serverPortsWellKnown.Contains((int)_options.ServerPort) || (int)_options.ServerPort >= 1024))
				{
					optionsInvalid.Add(string.Format("{0}:ServerPort ('{1}' is not a valid SMTP server port option)", SmtpWorkerOptions.SmtpConfiguration));
					optionsValidationFailed = true;
				}

			}
			else
			{
				optionsMissing.Add(string.Format("{0}:ServerPort", SmtpWorkerOptions.SmtpConfiguration));
				optionsValidationFailed = true;
			}

			// Validate AllowedSenderAddresses.
			if (_options.AllowedSenderAddresses is not null)
			{
				// TODO: Reconfigure SmtpWorkerOptions to pull actual IP/DNS name objects and use those for SmtpWorkerFilter instead?
				if (!_options.AllowedSenderAddresses.Any())
				{
					// TODO: Doesn't seem to ever be hit, as if the JSON array is present but empty in config file, AllowedSenderAddresses is null. Remove?
					optionsInvalid.Add(string.Format("{0}:AllowedSenderAddresses (no allowed sender addresses provided; relay would reject all incoming mail)", SmtpWorkerOptions.SmtpConfiguration));
					optionsValidationFailed = true;
				}

				_options.AllowedSenderAddresses.Sort();

				foreach (string address in _options.AllowedSenderAddresses)
				{
					if (!IPAddress.IsValid(address) && !Uri.CheckHostName(address).Equals(UriHostNameType.Dns))
					{
						optionsInvalid.Add(string.Format("{0}:AllowedSenderAddresses ('{1}' is not a valid IP address or DNS host name)", SmtpWorkerOptions.SmtpConfiguration, address));
						optionsValidationFailed = true;
					}
				}
			}
			else
			{
				optionsMissing.Add(string.Format("{0}:AllowedSenderAddresses", SmtpWorkerOptions.SmtpConfiguration));
				optionsValidationFailed = true;
			}

			// Determine if validation failed. If so, build and log error message indicating which options are missing or invalid.
			if (!optionsValidationFailed)
			{
				_logger.LogTrace(RelayLogEvents.SmtpWorkerValidated, "Worker configuration validated.");
			}
			else
			{
				StringBuilder optionsValidationFailedMessageBuilder = new();

				if (optionsMissing.Any())
				{
					foreach (string option in optionsMissing)
					{
						optionsValidationFailedMessageBuilder.AppendLine("\tMissing: " + option);
					}
					if (optionsInvalid.Any()) { optionsValidationFailedMessageBuilder.AppendLine(); }
				}

				if (optionsInvalid.Any())
				{
					foreach (string option in optionsInvalid)
					{
						optionsValidationFailedMessageBuilder.AppendLine("\tInvalid: " + option);
					}
				}

				if (optionsValidationFailedMessageBuilder.Length > 0)
				{
					string optionsValidationFailedMessage = optionsValidationFailedMessageBuilder.ToString();
					_logger.LogError(RelayLogEvents.SmtpWorkerValidationFailed, "Worker validation failed due to the following errors. Please review configuration file and documentation.\r\n{@optionsValidationFailedMessage}", optionsValidationFailedMessage);
				}
				else
				{
					_logger.LogCritical(RelayLogEvents.SmtpWorkerValidationFailedUnhandled, "Worker validation failed due to unhandled validation errors. Please contact the developer.");
				}
			}

			return !optionsValidationFailed;
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			// Stop called without start
			if (_workerTask == null)
			{
				return;
			}

			try
			{
				// Signal cancellation to the executing method
				_stoppingCts!.Cancel();

				// Stop the SMTP relay.
				_smtpServer!.Shutdown();
			}
			finally
			{
				// Wait until the task completes or the stop token triggers
				await Task.WhenAny(_workerTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
			}
		}

		public void Dispose()
		{
			// Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// TODO: dispose managed state (managed objects)
				}

				// TODO: free unmanaged resources (unmanaged objects) and override finalizer
				// TODO: set large fields to null
				disposedValue = true;
			}
		}
	}
}