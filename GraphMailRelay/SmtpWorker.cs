using MimeKit;
using SmtpServer;
using System.Threading.Channels;

namespace GraphMailRelay
{
	internal class SmtpWorker : IHostedService, IDisposable
	{
		private CancellationTokenSource? _stoppingCts;
		private Task? _workerTask;
		private bool disposedValue;

		private readonly ILogger<SmtpWorker> _logger;
		private readonly SmtpWorkerOptions _options;
		private readonly ChannelWriter<KeyValuePair<Guid, MimeMessage>> _queueWriter;

		private SmtpServer.SmtpServer? _smtpServer;

		public SmtpWorker(
			ILogger<SmtpWorker> logger,
			SmtpWorkerOptions optionsSmtp,
			Channel<KeyValuePair<Guid, MimeMessage>> queue)
		{
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

		async Task IHostedService.StartAsync(CancellationToken cancellationToken)
		{
			// Create linked token to allow cancelling executing task from provided token
			_stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			// Get name of worker for log messages.
			string workerName = GetType().Name;

			// Verify required configuration parameters are available.
			if (_options.ServerName is not null &&
				_options.ServerPort is not null &&
				_options.AllowedSenderAddresses is not null)
			{
				// Begin setup of the SmtpServer object that will handle receiving mail.
				_logger.LogDebug("Initializing {@workerName}...", workerName);

				var smtpServiceOptions = new SmtpServerOptionsBuilder()
					.ServerName(_options.ServerName)
					.Port(_options.ServerPort.GetValueOrDefault())
					.Build();

				var smtpServiceProvider = new SmtpServer.ComponentModel.ServiceProvider();
				smtpServiceProvider.Add(new SmtpWorkerFilter(_logger, _options));
				smtpServiceProvider.Add(new SmtpWorkerRelayStore(_queueWriter));

				_smtpServer = new SmtpServer.SmtpServer(smtpServiceOptions, smtpServiceProvider);

				// Start the SmtpServer.
				string serverName = _options.ServerName;
				int serverPort = _options.ServerPort.GetValueOrDefault();
				string serverWhitelist = string.Join(", ", _options.AllowedSenderAddresses.Select(address => address));
				_logger.LogInformation("Initialized {@workerName} at {@serverName}:{@serverPort}; incoming mail will be accepted from the following whitelisted endpoints: {@serverWhitelist}. Starting SMTP server.", workerName, serverName, serverPort, serverWhitelist);
				_workerTask = _smtpServer.StartAsync(_stoppingCts.Token);
			}
			else
			{
				// Write error message indicating that we're missing configuration parameters, then request that worker start be cancelled.
				_logger.LogError("Failed to initialize {@workerName}. One or more required server parameters are missing. Please review configuration file and documentation.", workerName);
				_stoppingCts.Cancel();
			}

			return;
		}

		async Task IHostedService.StopAsync(CancellationToken cancellationToken)
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

		void IDisposable.Dispose()
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