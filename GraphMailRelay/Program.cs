using Microsoft.Extensions.Logging.EventLog;
using MimeKit;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Threading.Channels;

namespace GraphMailRelay
{
	public class Program
	{
		private const string relayLogName = "JM-A21 GraphMailRelay";
		private const string relayLogSourceName = "GraphMailRelay";
		private const string relayServiceName = "GraphMailRelayService";

		private const string logTraceAppConfigStarted = "App configuration started.";
		private const string logTraceAppConfigFinished = "App configuration finished.";
		private const string logTraceAppLoggingConfigStarted = "App logging configuration started.";
		private const string logTraceAppLoggingConfigFinished = "App logging configuration finished.";
		private const string logTraceWorkerServiceConfigStarted = "Worker service configuration started.";
		private const string logTraceWorkerServiceConfigFinished = "Worker service configuration finished.";
		private const string LogTraceSmtpWorkerServiceConfigFound = "SMTP worker service configuration found.";
		private const string LogTraceGraphWorkerServiceConfigFound = "Graph worker service configuration found.";
		private const string logTraceWindowsServiceConfigStarted = "Windows service configuration started";
		private const string logTraceWindowsServiceConfigFinished = "Windows service configuration finished.";

		private const string logWarningWindowsCustomEventLogMissing = "The Windows Event Viewer on this machine does not contain the custom log '" + relayLogName + "'; event viewer logging may be impaired. This can be corrected automatically by running the application with administrative privileges, either once as standalone or via Windows Service with the appropriate log on configuration. Additionally, due to the missing event log, an 'Unable to log .NET application events' error may be written to the standard Application log. If you are reading this warning, that error may be ignored.";

		private const string logErrorSmtpWorkerConfigMissing = "Application failed to locate required configuration section name '" + SmtpWorkerOptions.SmtpConfiguration + "' across loaded configuration files. Application will shut down.";
		private const string logErrorGraphWorkerConfigMissing = "Application failed to locate required configuration section name '" + GraphWorkerOptions.GraphConfiguration + "' across loaded configuration files. Application will shut down.";
		private const string logErrorUnknown = "Application caught exception during service host startup";


		public static async Task Main(string[] args)
		{
			try
			{
				// Begin the host build chain.
				var host = Host
					.CreateDefaultBuilder(args)

					// Configure logging.
					.ConfigureLoggingWithHostBuilderLogger(logging =>
					{
						HostBuilderLogger.Logger.LogTrace(logTraceAppLoggingConfigStarted);

						// Configure Windows Event Viewer-specific log settings.
						logging.ClearProviders();
						logging.AddConsole();
						logging.AddEventLog(new EventLogSettings()
						{
							SourceName = relayLogSourceName,
							LogName = relayLogName
						});

						// Test for the existence of our targeted custom log name. Since the application is intended to run as a Windows Service in production the log
						// name / source configured above will be created automatically by .NET the first time the service is started, as the service will have administrative rights.
						// Additionally, the WiX installer may also be set up to create the log and source during installation. Both cases will result in a non-elevated instance
						// of the application being capable of writing events to the logs. However, until one of these two situations occurs, standalone and non-elevated instances
						// of the application may not be able to write to the event log. Because of this, write a warning that Event Viewer logging may be impaired.
						if (!EventLog.Exists(relayLogName))
						{
							HostBuilderLogger.Logger.LogWarning(logWarningWindowsCustomEventLogMissing);
						}

						HostBuilderLogger.Logger.LogTrace(logTraceAppLoggingConfigFinished);
					})

					// Add additional configuration files. 
					.ConfigureAppConfiguration(config =>
					{
						HostBuilderLogger.Logger.LogTrace(logTraceAppConfigStarted);

						// TODO: Make this dynamic. Ideally both application and WiX should be dynamically fed from properties somewhere. This must match what's configured in WiX, otherwise the file path will be wrong and the settings won't load.
						string pathAppSettingsExternal = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JM-A21", "GraphMailRelay", "appsettings.json");

#if DEBUG
						config.AddJsonFile(path: pathAppSettingsExternal, optional: true, reloadOnChange: false);
#else
						config.AddJsonFile(path: pathAppSettingsExternal, optional: false, reloadOnChange: false);
#endif

						HostBuilderLogger.Logger.LogTrace(logTraceAppConfigFinished);
					})

					.ConfigureServices((hostContext, services) =>
					{
						HostBuilderLogger.Logger.LogTrace(logTraceWorkerServiceConfigStarted);

						// Build and add the unbounded channel object we'll use as a producer/consumer work queue between SmtpWorker/GraphWorker.
						services.AddSingleton(
							Channel.CreateUnbounded<KeyValuePair<Guid, MimeMessage>>(
								new UnboundedChannelOptions
								{
									SingleWriter = false,
									SingleReader = true
								}
							)
						);

						// Attempt to retrieve the SmtpWorker settings. GetRequiredSection throws InvalidOperationException if section is missing. Then confirm the results are not null.
						var optionsSmtp = hostContext.Configuration.GetRequiredSection(SmtpWorkerOptions.SmtpConfiguration).Get<SmtpWorkerOptions>();
						if (optionsSmtp is not null)
						{
							HostBuilderLogger.Logger.LogTrace(LogTraceSmtpWorkerServiceConfigFound);
							services.AddSingleton(optionsSmtp);
							services.AddHostedService<SmtpWorker>();
						}
						else
						{
							throw new NullReferenceException(logErrorSmtpWorkerConfigMissing);
						}

						// Attempt to retrieve the GraphWorker settings. GetRequiredSection throws InvalidOperationException if section is missing. Then confirm the results are not null.
						var optionsGraph = hostContext.Configuration.GetRequiredSection(GraphWorkerOptions.GraphConfiguration).Get<GraphWorkerOptions>();
						if (optionsGraph is not null)
						{
							HostBuilderLogger.Logger.LogTrace(LogTraceGraphWorkerServiceConfigFound);
							services.AddSingleton(optionsGraph);
							services.AddHostedService<GraphWorker>();
							services.AddOpenTelemetry()
								.WithTracing((builder) =>
								{
									builder
										.SetResourceBuilder(
											ResourceBuilder
												.CreateDefault()
												.AddService(nameof(GraphWorker))
										)
										.AddSource(nameof(GraphWorker))
										.AddHttpClientInstrumentation(httpOptions =>
										{
											httpOptions.FilterHttpRequestMessage = (httpRequestMessage) =>
											{
												return optionsGraph.HttpResponseCapture.GetValueOrDefault();
											};

											httpOptions.EnrichWithHttpResponseMessage = (activity, httpResponseMessage) =>
											{
												if (httpResponseMessage is not null)
												{
													activity.SetTag("http.response_content", httpResponseMessage.Content.ReadAsStringAsync().Result);
												}
											};
										})
										.AddConsoleExporter();
								});
						}
						else
						{
							throw new NullReferenceException(logErrorGraphWorkerConfigMissing);
						}

						HostBuilderLogger.Logger.LogTrace(logTraceAppLoggingConfigFinished);
					})

					.UseWindowsService((options) =>
					{
						HostBuilderLogger.Logger.LogTrace(logTraceWindowsServiceConfigStarted);

						options.ServiceName = relayServiceName;

						HostBuilderLogger.Logger.LogTrace(logTraceWindowsServiceConfigFinished);
					})

					.Build();

				// Host application has been built, now start it.
				host.Run();
			}
			catch (Exception ex)
			{
				HostBuilderLogger.Logger.LogError(logErrorUnknown, ex);
				await HostBuilderLogger.Logger.TerminalEmitCachedMessages(args);
				return;
			}
		}
	}
}