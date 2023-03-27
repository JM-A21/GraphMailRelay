using MimeKit;
using System.Threading.Channels;

namespace GraphMailRelay
{
	internal class GraphWorker : IHostedService, IDisposable
	{
		private CancellationTokenSource? _stoppingCts;
		private Task? _workerTask;
		private bool disposedValue;

		private readonly ILogger<GraphWorker> _logger;
		private readonly GraphWorkerOptions _options;
		private readonly ChannelReader<KeyValuePair<Guid, MimeMessage>> _queueReader;
		private readonly Task _queueReaderCompletionTask;

		public GraphWorker(
			ILogger<GraphWorker> logger,
			GraphWorkerOptions options,
			Channel<KeyValuePair<Guid, MimeMessage>> queue)
		{
			_logger = logger;
			_options = options;
			_queueReader = queue.Reader;
			_queueReaderCompletionTask = queue.Reader.Completion;
		}

		// // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
		// ~GraphWorker()
		// {
		//     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
		//     Dispose(disposing: false);
		// }

		async Task IHostedService.StartAsync(CancellationToken cancellationToken)
		{
			// Create linked token to allow cancelling executing task from provided token
			_stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			// Create a new task representing the primary long-running operation for this worker.
			_workerTask = Task.Factory.StartNew(async () =>
			{
				while (!_stoppingCts.IsCancellationRequested | !_queueReaderCompletionTask.IsCompleted | await _queueReader.WaitToReadAsync())
				{
					while (_queueReader.TryRead(out KeyValuePair<Guid, MimeMessage> queueItem))
					{
						string messageId = queueItem.Key.ToString();
						_logger.LogInformation("Received message {@messageId} from relay channel!", messageId);
					}
				}
			});

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