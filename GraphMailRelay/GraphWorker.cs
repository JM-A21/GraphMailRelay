using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Authentication;
using Microsoft.Graph.Models;
using MimeKit;
using System.Net.Mail;
using System.Text;
using System.Threading.Channels;

namespace GraphMailRelay
{
	internal class GraphWorker : IHostedService, IDisposable
	{
		private CancellationTokenSource? _stoppingCts;
		private Task? _workerTask;
		private bool disposedValue;

		private readonly IHostApplicationLifetime _applicationLifetime;
		private readonly ILogger<GraphWorker> _logger;
		private readonly GraphWorkerOptions _options;
		private readonly ChannelReader<KeyValuePair<Guid, MimeMessage>> _queueReader;
		private readonly Task _queueReaderCompletionTask;
		private readonly List<string> _graphEnvironmentsSupported;

		private GraphServiceClient? _graphServiceClient;

		public GraphWorker(
			IHostApplicationLifetime applicationLifetime,
			ILogger<GraphWorker> logger,
			GraphWorkerOptions options,
			Channel<KeyValuePair<Guid, MimeMessage>> queue)
		{
			_applicationLifetime = applicationLifetime;
			_logger = logger;
			_options = options;
			_queueReader = queue.Reader;
			_queueReaderCompletionTask = queue.Reader.Completion;

			// TODO: Move to resource / external file somehow? Better way of doing this?
			_graphEnvironmentsSupported = new List<string>()
			{
				"GraphGlobal",
				"GraphUSGovL4"
			};
		}

		// // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
		// ~GraphWorker()
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

			// Return to host.
			return;
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

			if (_options.AzureTenantId is not null)
			{
				if (!Guid.TryParse(_options.AzureTenantId, out _))
				{
					optionsInvalid.Add(string.Format("{0}:AzureTenantId ('{1}' is not a valid GUID)", GraphWorkerOptions.GraphConfiguration, _options.AzureTenantId));
					optionsValidationFailed = true;
				}
			}
			else
			{
				optionsMissing.Add(string.Format("{0}:AzureTenantId", GraphWorkerOptions.GraphConfiguration));
				optionsValidationFailed = true;
			}

			if (_options.AzureClientId is not null)
			{
				if (!Guid.TryParse(_options.AzureClientId, out _))
				{
					optionsInvalid.Add(string.Format("{0}:AzureClientId ('{1}' is not a valid GUID)", GraphWorkerOptions.GraphConfiguration, _options.AzureClientId));
					optionsValidationFailed = true;
				}
			}
			else
			{
				optionsMissing.Add(string.Format("{0}:AzureClientId", GraphWorkerOptions.GraphConfiguration));
				optionsValidationFailed = true;
			}

			if (_options.AzureClientSecret is not null)
			{
				if (_options.AzureClientSecret.Length == 0)
				{
					optionsInvalid.Add(string.Format("{0}:AzureClientSecret (secret string is zero-length)", GraphWorkerOptions.GraphConfiguration));
					optionsValidationFailed = true;
				}
			}
			else
			{
				optionsMissing.Add(string.Format("{0}:AzureClientSecret", GraphWorkerOptions.GraphConfiguration));
				optionsValidationFailed = true;
			}

			if (_options.AzureMailUser is not null)
			{
				if (!Guid.TryParse(_options.AzureMailUser, out _) && !MailAddress.TryCreate(_options.AzureMailUser, out _))
				{
					optionsInvalid.Add(string.Format("{0}:AzureTenantId ('{1}' is not a valid GUID or user principal name)", GraphWorkerOptions.GraphConfiguration, _options.AzureMailUser));
					optionsValidationFailed = true;
				}
			}
			else
			{
				optionsMissing.Add(string.Format("{0}:AzureTenantId", GraphWorkerOptions.GraphConfiguration));
				optionsValidationFailed = true;
			}

			if (_options.GraphEnvironmentName is not null)
			{
				if (!_graphEnvironmentsSupported.Contains(_options.GraphEnvironmentName))
				{
					optionsInvalid.Add(string.Format("{0}:GraphEnvironmentName ('{1}' is not a supported Graph environment name)", GraphWorkerOptions.GraphConfiguration, _options.GraphEnvironmentName));
					optionsValidationFailed = true;
				}
			}
			else
			{
				optionsMissing.Add(string.Format("{0}:GraphEnvironmentName", GraphWorkerOptions.GraphConfiguration));
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

			// Begin setup of our Azure and Graph objects.
			_logger.LogDebug("Initializing {@workerName}...", workerName);

			Uri azureAuthorityHost;
			Uri graphBaseUri;

			switch (_options.GraphEnvironmentName)
			{
				case "GraphGlobal":
					azureAuthorityHost = AzureAuthorityHosts.AzurePublicCloud;
					graphBaseUri = new("https://graph.microsoft.com/v1.0");
					break;

				case "GraphUSGovL4":
					azureAuthorityHost = AzureAuthorityHosts.AzureGovernment;
					graphBaseUri = new("https://graph.microsoft.us/v1.0");
					break;

				default:
					throw new InvalidOperationException(string.Format("Unsupported Graph environment name."));
			}

			var azureHandlers = GraphClientFactory.CreateDefaultHandlers();
			var azureCredentials = new ClientSecretCredential(
				tenantId: _options.AzureTenantId,
				clientId: _options.AzureClientId,
				clientSecret: _options.AzureClientSecret,
				options: new TokenCredentialOptions
				{
					AuthorityHost = azureAuthorityHost
				});

			var azureHttpClient = GraphClientFactory.Create(azureHandlers);
			var azureAuthProvider = new AzureIdentityAuthenticationProvider(azureCredentials);
			_graphServiceClient = new GraphServiceClient(azureHttpClient, azureAuthProvider, graphBaseUri.ToString());

			// TODO: Validate that provide AzureMailUser exists and is valid so relay doesn't choke when trying to send real messages after startup.

			// Create a new task representing the primary long-running operation for this worker.
			_workerTask = Task.Factory.StartNew(async () =>
			{
				while (!_stoppingCts!.IsCancellationRequested | !_queueReaderCompletionTask.IsCompleted | await _queueReader.WaitToReadAsync())
				{
					while (_queueReader.TryRead(out KeyValuePair<Guid, MimeMessage> queueItem))
					{
						Guid messageId = queueItem.Key;
						MimeMessage message = queueItem.Value;

						using var mimeStream = new MemoryStream();
						message.WriteTo(mimeStream);
						mimeStream.Position = 0;

						var graphMailRequest = _graphServiceClient
							.Users[_options.AzureMailUser]
							.SendMail
							.ToPostRequestInformation(
								new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody()
							);

						// This shouldn't be necessary, but since I'm not sure how to strip off the original content-type
						// header using the SDK itself, just manually remove it and add the proper one for now as a workaround.
						// TODO: Determine how to make this adjustment via the SDK itself.
						graphMailRequest.Headers.Remove("Content-Type");
						graphMailRequest.Headers.Add("Content-Type", "text/plain");

						graphMailRequest.HttpMethod = Microsoft.Kiota.Abstractions.Method.POST;
						graphMailRequest.Content = new StringContent(Convert.ToBase64String(mimeStream.ToArray()), Encoding.UTF8, "text/plain").ReadAsStream();

						try
						{
							string messageIdString = messageId.ToString();
							string messageToString = string.Join(", ", message.To);

							_logger.LogDebug("{@workerName} is attempting to send message '{@messageId}' to recipient(s) '{@messageToString}'", workerName, messageIdString, messageToString);
							Task<MessageCollectionResponse?> requestTask = _graphServiceClient.RequestAdapter.SendAsync<MessageCollectionResponse>(graphMailRequest, MessageCollectionResponse.CreateFromDiscriminatorValue);
							requestTask.Wait();

							switch (requestTask.Status)
							{
								case System.Threading.Tasks.TaskStatus.RanToCompletion:
									_logger.LogInformation("{@workerName} successfully sent message '{@messageId}' to recipient(s) '{@messageToString}'", workerName, messageIdString, messageToString);
									break;

								case System.Threading.Tasks.TaskStatus.Canceled:
									_logger.LogWarning("{@workerName} has dropped '{@messageId}' to recipient(s) '{@messageToString}' due to task cancellation.", workerName, messageIdString, messageToString);
									break;

								case System.Threading.Tasks.TaskStatus.Faulted:
									_logger.LogError("{@workerName} has dropped '{@messageId}' to recipient(s) '{@messageToString}' due to unexpected task fault. Please review logs and contact the developer.", workerName, messageIdString, messageToString);
									break;
							}
						}
						catch (Exception ex)
						{
							// TODO: Fix this somehow.
							_logger.LogCritical(ex, null);
						}

					}
				}
			}, _stoppingCts!.Token);

			_logger.LogInformation("Initialized {@workerName}.", workerName);
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