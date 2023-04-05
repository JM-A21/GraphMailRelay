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
		private const string logTraceMessageDequeued = "Message received from relay queue.";
		private const string logTraceRequestBuildStarted = "Building Graph API request.";
		private const string logTraceRequestBuildFromOverrideStarted = "Starting 'From' address override per application configuration.";
		private const string logTraceRequestBuildFromOverrideParsed = "Parsed 'From' sender address.";
		private const string logTraceRequestBuildFromOverrideRequired = "Message 'From' address does not match override address. Sender address will be overridden.";
		private const string logTraceRequestBuildFromOverrideSkipped = "Message 'From' address matches override address. No action is required, skipping override.";
		private const string logTraceRequestBuildFromOverrideComplete = "Overriding 'From' address per application configuration.";
		private const string logTraceRequestBuildFinished = "Finished building Graph API request.";

		private const string logDebugRequestSending = "Sending Graph API request.";

		private const string logInfoRequestComplete = "Successfully completed Graph API request.";

		private const string logWarningRequestBuildFailedMultipleFromAddresses = "Dropped relay message due to multiple 'From' addresses being specified in message while the Sender address was null. The endpoint sending the email address may be formatting the message incorrectly.";
		private const string logWarningRequestBuildFromOverrideParsingFailed = "Dropped relay message due to 'From' address override failure. Unable to parse FromAddressOverride specified in application configuration.";
		private const string logWarningRequestCanceled = "Dropped relay message due to unexpected Graph API request cancellation.";

		private const string logErrorRequestFaulted = "Dropped relay message due to unexpected Graph API request fault. Please contact the developer.";
		private const string logErrorUnknown = "Worker caught exception during Graph API request processing.";

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
			_logger.LogTrace(RelayLogEvents.GraphWorkerStarting, "Worker is starting.");

			// Create linked token to allow cancelling executing task from provided token
			_stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			// Confirm provided configuration parameters are valid.
			if (ValidateOptions())
			{
				Uri azureAuthorityHost;
				Uri graphBaseUri;

				switch (_options.EnvironmentName)
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
						throw new InvalidOperationException("Unsupported Graph environment name.");
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

							using (_logger.BeginScope(new Dictionary<string, object>
							{
								{ "MessageId", messageId.ToString() },
								{ "Sender", message.Sender },
								{ "From", message.From },
								{ "Recipients", message.To }
							}))
							{
								_logger.LogTrace(RelayLogEvents.GraphWorkerMessageDequeued, logTraceMessageDequeued);

								try
								{
									if (_options.FromAddressOverride is not null & _options.FromAddressOverride != string.Empty)
									{
										_logger.LogTrace(RelayLogEvents.GraphWorkerRequestBuildFromOverrideStarted, logTraceRequestBuildFromOverrideStarted);

										if (MailboxAddress.TryParse(_options.FromAddressOverride, out MailboxAddress addressOverride))
										{
											_logger.LogTrace(RelayLogEvents.GraphWorkerRequestBuildFromOverrideParsed, logTraceRequestBuildFromOverrideParsed);

											if (message.Sender is not null)
											{
												// Override the Sender address.
												if (message.Sender.Address != addressOverride.Address)
												{
													_logger.LogTrace(RelayLogEvents.GraphWorkerRequestBuildFromOverrideRequired, logTraceRequestBuildFromOverrideRequired);

													message.Sender = addressOverride;

													_logger.LogTrace(RelayLogEvents.GraphWorkerRequestBuildFromOverrideComplete, logTraceRequestBuildFromOverrideComplete);
												}
												else
												{
													_logger.LogTrace(RelayLogEvents.GraphWorkerRequestBuildFromOverrideSkipped, logTraceRequestBuildFromOverrideSkipped);
												}
											}
											else
											{
												// Theoretically we should only have 1 address if the Sender address is null, but confirm anyways so we can 
												// handle it and log a warning if not. This could mean the application isn't formatting the outgoing email
												// properly. 
												if (message.From.Count == 1)
												{
													if (((MailboxAddress)message.From.First()).Address != addressOverride.Address)
													{
														_logger.LogTrace(RelayLogEvents.GraphWorkerRequestBuildFromOverrideRequired, logTraceRequestBuildFromOverrideRequired);

														message.From.Clear();
														message.From.Add(addressOverride);

														_logger.LogTrace(RelayLogEvents.GraphWorkerRequestBuildFromOverrideComplete, logTraceRequestBuildFromOverrideComplete);
													}
													else
													{
														_logger.LogTrace(RelayLogEvents.GraphWorkerRequestBuildFromOverrideSkipped, logTraceRequestBuildFromOverrideSkipped);
													}
												}
												else
												{
													_logger.LogWarning(RelayLogEvents.GraphWorkerRequestBuildFailedMultipleFromAddresses, logWarningRequestBuildFailedMultipleFromAddresses);
												}
											}

										}
										else
										{
											_logger.LogWarning(RelayLogEvents.GraphWorkerRequestBuildFromOverrideParsingFailed, logWarningRequestBuildFromOverrideParsingFailed);
										}
									}

									using var mimeStream = new MemoryStream();
									message.WriteTo(mimeStream);
									mimeStream.Position = 0;

									_logger.LogTrace(RelayLogEvents.GraphWorkerRequestBuildStarted, logTraceRequestBuildStarted);
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

									_logger.LogTrace(RelayLogEvents.GraphWorkerRequestBuildFinished, logTraceRequestBuildFinished);

									_logger.LogDebug(RelayLogEvents.GraphWorkerRequestSending, logDebugRequestSending);

									Task<MessageCollectionResponse?> requestTask = _graphServiceClient.RequestAdapter.SendAsync<MessageCollectionResponse>(graphMailRequest, MessageCollectionResponse.CreateFromDiscriminatorValue);
									requestTask.Wait();

									switch (requestTask.Status)
									{
										case System.Threading.Tasks.TaskStatus.RanToCompletion:
											_logger.LogInformation(RelayLogEvents.GraphWorkerRequestComplete, logInfoRequestComplete);
											break;

										case System.Threading.Tasks.TaskStatus.Canceled:
											_logger.LogWarning(RelayLogEvents.GraphWorkerRequestTaskCanceled, logWarningRequestCanceled);
											break;

										case System.Threading.Tasks.TaskStatus.Faulted:
											_logger.LogError(RelayLogEvents.GraphWorkerRequestTaskFaulted, logErrorRequestFaulted);
											break;
									}
								}
								catch (Exception ex)
								{
									_logger.LogError(RelayLogEvents.GraphWorkerUnknownError, ex, logErrorUnknown);
								}
							}
						}
					}
				}, _stoppingCts!.Token);

				if (_options.FromAddressOverride is not null & _options.FromAddressOverride != string.Empty)
				{
					string fromAddressOverride = _options.FromAddressOverride!;
					_logger.LogInformation(RelayLogEvents.GraphWorkerStarted, "Worker has started. Outgoing Graph API requests will have the Sender/From address overridden to {fromAddressOverride}' in accordance with application configuration.", fromAddressOverride);
				}
				else
				{
					_logger.LogInformation(RelayLogEvents.GraphWorkerStarted, "Worker has started.");
				}
			}
			else
			{
				_logger.LogTrace(RelayLogEvents.GraphWorkerCancelling, "Worker is requesting application halt.");
				_applicationLifetime.StopApplication();
			}
		}

		private bool ValidateOptions()
		{
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

			if (_options.EnvironmentName is not null)
			{
				if (!_graphEnvironmentsSupported.Contains(_options.EnvironmentName))
				{
					optionsInvalid.Add(string.Format("{0}:EnvironmentName ('{1}' is not a supported Graph environment name)", GraphWorkerOptions.GraphConfiguration, _options.EnvironmentName));
					optionsValidationFailed = true;
				}
			}
			else
			{
				optionsMissing.Add(string.Format("{0}:EnvironmentName", GraphWorkerOptions.GraphConfiguration));
				optionsValidationFailed = true;
			}


			// This may be null/empty, in which case it won't be used, so don't fail validation if it is.
			if (_options.FromAddressOverride is not null & _options.FromAddressOverride != string.Empty)
			{
				// Verify its a valid email address.
				if (!MailAddress.TryCreate(_options.FromAddressOverride, out _))
				{
					optionsInvalid.Add(string.Format("{0}:FromAddressOverride ('{1}' is not a valid email address)", GraphWorkerOptions.GraphConfiguration, _options.FromAddressOverride));
					optionsValidationFailed = true;
				}
			}

			// Determine if validation failed. If so, build and log error message indicating which options are missing or invalid.
			if (optionsValidationFailed)
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
					_logger.LogError(RelayLogEvents.GraphWorkerValidationFailed, "Worker validation failed due to the following errors. Please review configuration file and documentation.\r\n{@optionsValidationFailedMessage}", optionsValidationFailedMessage);
				}
				else
				{
					_logger.LogCritical(RelayLogEvents.GraphWorkerValidationFailedUnhandled, "Worker validation failed due to unhandled validation errors. Please contact the developer.");
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