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
			// Create linked token to allow cancelling executing task from provided token
			_stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			// Confirm provided configuration parameters are valid.
			ValidateOptions();

			// Initialize worker operations.
			InitializeWorker();
		}

		private bool ValidateOptions()
		{
			// Get the name of this worker class.
			string workerName = GetType().Name;

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
				_options.AllowedSenderAddresses.Sort();

				// TODO: Reconfigure SmtpWorkerOptions to pull actual IP/DNS name objects and use those for SmtpWorkerFilter instead?
				if (!_options.AllowedSenderAddresses.Any())
				{
					// TODO: Doesn't seem to ever be hit, as if the JSON array is present but empty in config file, AllowedSenderAddresses is null. Remove?
					optionsInvalid.Add(string.Format("{0}:AllowedSenderAddresses (no allowed sender addresses provided; relay would reject all incoming mail)", SmtpWorkerOptions.SmtpConfiguration));
					optionsValidationFailed = true;
				}

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
			if (optionsValidationFailed)
			{
				StringBuilder optionsValidationFailedMessageBuilder = new();

				if (optionsMissing.Any())
				{
					foreach (string option in optionsMissing)
					{
						optionsValidationFailedMessageBuilder.AppendLine("Missing: " + option);
					}
					if (optionsInvalid.Any()) { optionsValidationFailedMessageBuilder.AppendLine(); }
				}

				if (optionsInvalid.Any())
				{
					foreach (string option in optionsInvalid)
					{
						optionsValidationFailedMessageBuilder.AppendLine("Invalid: " + option);
					}
				}

				string optionsValidationFailedMessage = optionsValidationFailedMessageBuilder.ToString();

				if (optionsValidationFailedMessageBuilder.Length > 0)
				{
					_logger.LogError("Failed to initialize {@workerName} due to the following settings validation failures. Please review configuration file and documentation.\r\n\r\n{@optionsValidationFailedMessage}", workerName, optionsValidationFailedMessage);
				}
				else
				{
					_logger.LogCritical("Failed to initialize {@workerName} due to unhandled settings validation failures. Please contact the developer.", workerName);
				}
				_applicationLifetime.StopApplication();

			}

			return !optionsValidationFailed;
		}

		private void InitializeWorker()
		{
			// Get the name of this worker class.
			string workerName = GetType().Name;

			// Begin setup of the SmtpServer object that will handle receiving mail.
			_logger.LogDebug("Initializing {@workerName}...", workerName);

			var smtpServiceOptions = new SmtpServerOptionsBuilder()
				.ServerName(_options.ServerName!)
				.Port((int)_options.ServerPort!)
				.Build();

			var smtpServiceProvider = new SmtpServer.ComponentModel.ServiceProvider();
			smtpServiceProvider.Add(new SmtpWorkerFilter(_logger, _options));
			smtpServiceProvider.Add(new SmtpWorkerRelayStore(_queueWriter));

			_smtpServer = new SmtpServer.SmtpServer(smtpServiceOptions, smtpServiceProvider);

			// Start the SmtpServer.
			_workerTask = _smtpServer.StartAsync(_stoppingCts.Token);

			string serverWhitelist = string.Join(", ", _options.AllowedSenderAddresses!.Select(address => address));
			string serverName = _options.ServerName!;
			int serverPort = (int)_options.ServerPort!;
			_logger.LogInformation("Initialized {@workerName} listening on {@serverName}:{@serverPort}; incoming mail will be accepted from the following whitelisted endpoints: {@serverWhitelist}.", workerName, serverName, serverPort, serverWhitelist);
			return;
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